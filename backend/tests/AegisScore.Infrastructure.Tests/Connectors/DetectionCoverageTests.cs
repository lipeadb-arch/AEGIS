using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Connectors.Google.SecOps;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;
using RecordingHandler = AegisScore.Infrastructure.Tests.Connectors.ChronicleApiClientTests.RecordingHandler;
using AppCoverage = AegisScore.Application.Abstractions.DetectionCoverageSnapshot;

namespace AegisScore.Infrastructure.Tests.Connectors;

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-02] Catálogo MITRE FAKE (mínimo) para os testes de cobertura de detecção — evita
/// depender do artefato de 800+ técnicas. Só o necessário: T1059(+.003), T1110, T1566 correntes; T9999 inexistente.
/// </summary>
internal sealed class FakeMitreCatalog : IMitreAttackCatalog
{
    private readonly Dictionary<string, MitreTechnique> _t = new(StringComparer.Ordinal)
    {
        ["T1059"] = new("T1059", "Command and Scripting Interpreter", false, null, new[] { "TA0002" }),
        ["T1059.003"] = new("T1059.003", "Windows Command Shell", true, "T1059", new[] { "TA0002" }),
        ["T1110"] = new("T1110", "Brute Force", false, null, new[] { "TA0006" }),
        ["T1566"] = new("T1566", "Phishing", false, null, new[] { "TA0001" }),
    };

    private readonly Dictionary<string, MitreTactic> _ta = new(StringComparer.Ordinal)
    {
        ["TA0001"] = new("TA0001", "initial-access", "Initial Access", "Acesso Inicial"),
        ["TA0002"] = new("TA0002", "execution", "Execution", "Execução"),
        ["TA0006"] = new("TA0006", "credential-access", "Credential Access", "Acesso a Credenciais"),
    };

    public string AttackVersion => "17.1";
    public string DisplayLabel => "MITRE ATT&CK Enterprise v17.1 — alinhado à versão 17 suportada pelo Google SecOps.";
    public int ActiveTechniqueCount => _t.Count;
    public MitreTechnique? GetTechnique(string? id) =>
        id is not null && _t.TryGetValue(id.Trim().ToUpperInvariant(), out var t) ? t : null;
    public MitreTactic? GetTactic(string? id) =>
        id is not null && _ta.TryGetValue(id.Trim().ToUpperInvariant(), out var t) ? t : null;
}

/// <summary>[AEGIS-MVP-GOOGLE-SECOPS-02] Fixtures de regra CONFIG_ONLY para os testes de transporte e agregação.</summary>
internal static class RuleFixtures
{
    /// <summary>Regra CONFIG_ONLY: só campos de CONFIGURAÇÃO. <paramref name="techniqueMeta"/> vai no mapa metadata.technique.</summary>
    public static string Rule(
        string name, bool archived = false, bool live = true, bool alerting = true,
        string? techniqueMeta = null, string? mitreAttackTechnique = null, string[]? tags = null,
        bool? deploymentEnabled = null, bool? deploymentAlerting = null)
    {
        var parts = new List<string> { $"\"name\":\"{name}\"", $"\"archived\":{(archived ? "true" : "false")}" };
        if (deploymentEnabled is null) parts.Add($"\"liveModeEnabled\":{(live ? "true" : "false")}");
        if (deploymentAlerting is null) parts.Add($"\"alertingEnabled\":{(alerting ? "true" : "false")}");
        var meta = new List<string>();
        if (techniqueMeta is not null) meta.Add($"\"technique\":\"{techniqueMeta}\"");
        if (mitreAttackTechnique is not null) meta.Add($"\"mitre_attack_technique\":\"{mitreAttackTechnique}\"");
        if (meta.Count > 0) parts.Add("\"metadata\":{" + string.Join(",", meta) + "}");
        if (tags is not null) parts.Add("\"tags\":[" + string.Join(",", tags.Select(t => $"\"{t}\"")) + "]");
        if (deploymentEnabled is not null || deploymentAlerting is not null)
        {
            var dep = new List<string>();
            if (deploymentEnabled is not null) dep.Add($"\"enabled\":{(deploymentEnabled.Value ? "true" : "false")}");
            if (deploymentAlerting is not null) dep.Add($"\"alerting\":{(deploymentAlerting.Value ? "true" : "false")}");
            parts.Add("\"deployment\":{" + string.Join(",", dep) + "}");
        }
        return "{" + string.Join(",", parts) + "}";
    }

    public static string Page(IEnumerable<string> rules, string? next)
    {
        var body = "{\"rules\":[" + string.Join(",", rules) + "]";
        if (next is not null) body += ",\"nextPageToken\":\"" + next + "\"";
        return body + "}";
    }

    public static string List(params string[] rules) => "{\"rules\":[" + string.Join(",", rules) + "]}";
}

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-02] Transporte rules.list (CONFIG_ONLY) por HTTP SIMULADO: rota/query fixas, paginação
/// por nextPageToken (params estáveis), ciclo/teto de páginas/itens → parcial preservando o piso, falha na 1ª página
/// lança, falha após página vira parcial, cancelamento e higiene do bearer.
/// </summary>
public sealed class ChronicleRulesApiTests
{
    private const string Token = "fake-token";

    private static async Task<(int Count, ChronicleRulesResult Result)> Collect(ChronicleApiClient client)
    {
        var count = 0;
        var r = await client.CollectRulesAsync(Token, "proj", "us", "inst-1", _ => count++, CancellationToken.None);
        return (count, r);
    }

    [Fact]
    public async Task CollectRules_UsesConfigOnlyView_And5000PageSize_OnRulesPath()
    {
        string? url = null;
        var handler = new RecordingHandler(req => { url = req.RequestUri!.AbsoluteUri; return (HttpStatusCode.OK, RuleFixtures.List(), null); });
        var client = new ChronicleApiClient(new HttpClient(handler));

        await client.CollectRulesAsync(Token, "proj", "us", "inst-1", _ => { }, CancellationToken.None);

        handler.Paths.Should().ContainSingle().Which.Should().StartWith("/v1alpha/").And.EndWith("/rules");
        url.Should().Contain("view=CONFIG_ONLY").And.Contain("pageSize=5000");
        handler.Hosts.Should().OnlyContain(h => h == "us-chronicle.googleapis.com");
        handler.BearerSeen.Should().OnlyContain(b => b);
    }

    [Fact]
    public async Task CollectRules_FollowsPageToken_KeepsParamsStable_Complete()
    {
        var urls = new List<string>();
        var handler = new RecordingHandler(req =>
        {
            urls.Add(req.RequestUri!.AbsoluteUri);
            return req.RequestUri!.AbsoluteUri.Contains("pageToken=TOK2")
                ? (HttpStatusCode.OK, RuleFixtures.Page(new[] { RuleFixtures.Rule("ru/2") }, next: null), null)
                : (HttpStatusCode.OK, RuleFixtures.Page(new[] { RuleFixtures.Rule("ru/1") }, next: "TOK2"), null);
        });
        var (count, result) = await Collect(new ChronicleApiClient(new HttpClient(handler)));

        count.Should().Be(2);
        result.IsPartial.Should().BeFalse();
        urls.Should().OnlyContain(u => u.Contains("view=CONFIG_ONLY") && u.Contains("pageSize=5000"),
            "os parâmetros view/pageSize permanecem idênticos na paginação — só pageToken muda");
    }

    [Fact]
    public async Task CollectRules_EmptyArrayOmitted_IsEmptyLegit_NotPartial()
    {
        var (count, result) = await Collect(new ChronicleApiClient(new HttpClient(new RecordingHandler(_ => (HttpStatusCode.OK, "{}", null)))));
        count.Should().Be(0);
        result.IsPartial.Should().BeFalse("resposta completa sem regras é vazio REAL, não falha");
    }

    [Fact]
    public async Task CollectRules_RepeatedPageToken_PreservesFloor_Partial()
    {
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, RuleFixtures.Page(new[] { RuleFixtures.Rule("ru/x") }, next: "SAME"), null));
        var (count, result) = await Collect(new ChronicleApiClient(new HttpClient(handler)));
        result.LimitHit.Should().BeTrue("ciclo de pageToken → parcial, sem lançar");
        count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CollectRules_PageCap_PreservesFloor_Partial()
    {
        var i = 0;
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, RuleFixtures.Page(new[] { RuleFixtures.Rule($"ru/{i}") }, next: $"T{i++}"), null));
        var client = new ChronicleApiClient(new HttpClient(handler), 10, 1000, 1_000_000, 1000, rulesMaxPages: 3, rulesMaxItems: 1000);
        var (count, result) = await Collect(client);
        result.LimitHit.Should().BeTrue("teto de páginas → parcial");
        count.Should().Be(3, "as 3 páginas válidas são preservadas");
    }

    [Fact]
    public async Task CollectRules_ItemCap_PreservesFloor_Partial()
    {
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK,
            RuleFixtures.Page(new[] { RuleFixtures.Rule("ru/1"), RuleFixtures.Rule("ru/2"), RuleFixtures.Rule("ru/3") }, next: null), null));
        var client = new ChronicleApiClient(new HttpClient(handler), 10, 1000, 1_000_000, 1000, rulesMaxItems: 2);
        var (count, result) = await Collect(client);
        result.LimitHit.Should().BeTrue("teto de itens → parcial");
        count.Should().Be(2);
    }

    [Fact]
    public async Task CollectRules_FirstPageFailure_Throws()
    {
        var handler = new RecordingHandler(_ => (HttpStatusCode.Forbidden, "{}", null));
        var act = async () => await new ChronicleApiClient(new HttpClient(handler))
            .CollectRulesAsync(Token, "proj", "us", "inst-1", _ => { }, CancellationToken.None);
        (await act.Should().ThrowAsync<ChronicleApiException>()).Which.Kind.Should().Be(ChronicleApiErrorKind.InsufficientPermission);
    }

    [Fact]
    public async Task CollectRules_FailureAfterValidPage_PreservesFloor_Partial()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("pageToken=TOK2")
                ? (HttpStatusCode.InternalServerError, "{}", null)
                : (HttpStatusCode.OK, RuleFixtures.Page(new[] { RuleFixtures.Rule("ru/1") }, next: "TOK2"), null));
        var (count, result) = await Collect(new ChronicleApiClient(new HttpClient(handler)));
        result.FailedAfterFirstPage.Should().BeTrue("falha após uma página válida → parcial, sem descartar o piso");
        result.IsPartial.Should().BeTrue();
        count.Should().Be(1);
    }
}

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-02] Agregação de cobertura de detecção no conector (HTTP simulado): dedup por regra,
/// múltiplas técnicas, técnica/subtécnica, tag inválida/inexistente no catálogo, regra arquivada, live/alerting,
/// coleta completa vazia, falha da fonte → estado Unavailable (SEM lançar), zero sinais de score.
/// </summary>
public sealed class DetectionCoverageCollectorTests
{
    private const string Sa = "{\\\"type\\\":\\\"service_account\\\"}";
    private static readonly string Settings =
        "{\"projectId\":\"demo-secops\",\"location\":\"us\",\"instanceId\":\"inst-123\",\"serviceAccountJson\":\"" + Sa + "\"}";

    private static ConnectorConfig Config() => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.Parse("aa000000-0000-0000-0000-000000000003"),
        Provider = ConnectorProvider.Google,
        Capability = ConnectorCapability.Siem,
        AuthType = ConnectorAuthType.ServiceAccount,
        Enabled = true,
        EncryptedSettings = Settings,
    };

    private static GoogleSecOpsConnector Connector(Func<HttpRequestMessage, (HttpStatusCode, string, string?)> route) =>
        new(new FakeAuth(), new ChronicleApiClient(new HttpClient(new RecordingHandler(route))),
            new PassThrough(), new FakeMitreCatalog());

    private static Task<AppCoverage> Collect(Func<HttpRequestMessage, (HttpStatusCode, string, string?)> route) =>
        Connector(route).CollectCoverageAsync(Config(), CancellationToken.None);

    private static (HttpStatusCode, string, string?) Ok(string body) => (HttpStatusCode.OK, body, null);

    [Fact]
    public async Task Coverage_Available_TotalsAndPerTechniqueAggregates()
    {
        var body = RuleFixtures.List(
            RuleFixtures.Rule("ru/a", live: true, alerting: true, techniqueMeta: "T1059"),
            RuleFixtures.Rule("ru/b", live: true, alerting: false, techniqueMeta: "T1059"),
            RuleFixtures.Rule("ru/c", live: false, alerting: false, techniqueMeta: "T1110"),
            RuleFixtures.Rule("ru/d", live: true, alerting: true));   // sem técnica MITRE

        var snap = await Collect(_ => Ok(body));

        snap.State.Should().Be(DetectionCoverageCollectionState.Available);
        snap.Source.Should().Be("Google SecOps");
        snap.AttackVersion.Should().Be("17.1");
        snap.TotalActiveRules.Should().Be(4);
        snap.RulesWithMitre.Should().Be(3);
        snap.RulesWithoutMitre.Should().Be(1);
        snap.RulesInLiveMode.Should().Be(3);
        snap.RulesWithAlerting.Should().Be(2);

        snap.Techniques.Should().HaveCount(2);
        var t1059 = snap.Techniques.Single(t => t.TechniqueId == "T1059");
        t1059.Name.Should().Be("Command and Scripting Interpreter");
        t1059.RuleCount.Should().Be(2);
        t1059.LiveRuleCount.Should().Be(2);
        t1059.AlertingRuleCount.Should().Be(1);
        var t1110 = snap.Techniques.Single(t => t.TechniqueId == "T1110");
        t1110.RuleCount.Should().Be(1);
        t1110.LiveRuleCount.Should().Be(0, "regra pausada — técnica com regra mas sem execução");
    }

    [Fact]
    public async Task Coverage_RuleWithMultipleTechniques_CountsEach_DedupPerRule()
    {
        // Uma regra declara duas técnicas (T1059 e T1110), e T1059 aparece DUPLICADA na mesma regra → conta uma vez.
        var body = RuleFixtures.List(RuleFixtures.Rule("ru/multi", techniqueMeta: "T1059, T1110, T1059"));
        var snap = await Collect(_ => Ok(body));

        snap.RulesWithMitre.Should().Be(1);
        snap.Techniques.Select(t => t.TechniqueId).Should().BeEquivalentTo(new[] { "T1059", "T1110" });
        snap.Techniques.Should().OnlyContain(t => t.RuleCount == 1, "a técnica duplicada na mesma regra não conta duas vezes");
    }

    [Fact]
    public async Task Coverage_Subtechnique_IsCarried()
    {
        var snap = await Collect(_ => Ok(RuleFixtures.List(RuleFixtures.Rule("ru/sub", techniqueMeta: "T1059.003"))));
        var t = snap.Techniques.Single();
        t.TechniqueId.Should().Be("T1059.003");
        t.IsSubtechnique.Should().BeTrue();
        t.ParentTechniqueId.Should().Be("T1059");
    }

    [Fact]
    public async Task Coverage_MitreAttackTechniqueValueForm_IsParsed()
    {
        // Formato "T#### - Nome" na chave mitre_attack_technique — extrai só o ID e valida no catálogo.
        var snap = await Collect(_ => Ok(RuleFixtures.List(
            RuleFixtures.Rule("ru/x", techniqueMeta: null, mitreAttackTechnique: "T1110 - Brute Force"))));
        snap.RulesWithMitre.Should().Be(1);
        snap.Techniques.Single().TechniqueId.Should().Be("T1110");
    }

    [Fact]
    public async Task Coverage_TagsArray_IsScanned()
    {
        var snap = await Collect(_ => Ok(RuleFixtures.List(
            RuleFixtures.Rule("ru/tags", techniqueMeta: null, tags: new[] { "T1566", "internal-label" }))));
        snap.RulesWithMitre.Should().Be(1);
        snap.Techniques.Single().TechniqueId.Should().Be("T1566");
    }

    [Fact]
    public async Task Coverage_InvalidTag_And_UnknownTechnique_CountAsWithoutMitre_NoInvention()
    {
        // "TA0002" (tática, não técnica), "not-a-technique" e "T9999" (não existe no catálogo) → nenhuma técnica válida.
        var body = RuleFixtures.List(
            RuleFixtures.Rule("ru/bad", techniqueMeta: "TA0002", tags: new[] { "not-a-technique" }),
            RuleFixtures.Rule("ru/unknown", techniqueMeta: "T9999"));
        var snap = await Collect(_ => Ok(body));

        snap.TotalActiveRules.Should().Be(2);
        snap.RulesWithMitre.Should().Be(0);
        snap.RulesWithoutMitre.Should().Be(2, "tag inválida ou técnica inexistente no catálogo não vira técnica");
        snap.Techniques.Should().BeEmpty();
        snap.State.Should().Be(DetectionCoverageCollectionState.Available, "uma tag inválida não quebra a coleta inteira");
    }

    [Fact]
    public async Task Coverage_ArchivedRule_ExcludedFromActiveTotals()
    {
        var body = RuleFixtures.List(
            RuleFixtures.Rule("ru/live", techniqueMeta: "T1059"),
            RuleFixtures.Rule("ru/arch", archived: true, techniqueMeta: "T1110"));
        var snap = await Collect(_ => Ok(body));

        snap.TotalActiveRules.Should().Be(1, "regra arquivada não entra nos totais ativos");
        snap.Techniques.Should().ContainSingle().Which.TechniqueId.Should().Be("T1059");
    }

    [Fact]
    public async Task Coverage_DeploymentNestedFlags_AreRead()
    {
        // live/alerting vindos do sub-objeto deployment (enabled/alerting) em vez dos campos de topo.
        var body = RuleFixtures.List(
            RuleFixtures.Rule("ru/dep", techniqueMeta: "T1059", deploymentEnabled: true, deploymentAlerting: false));
        var snap = await Collect(_ => Ok(body));
        snap.RulesInLiveMode.Should().Be(1);
        snap.RulesWithAlerting.Should().Be(0);
    }

    [Fact]
    public async Task Coverage_DedupsSameRuleName_AcrossPages()
    {
        // A MESMA regra (mesmo name) aparece duas vezes → conta uma vez.
        var body = RuleFixtures.List(
            RuleFixtures.Rule("ru/same", techniqueMeta: "T1059"),
            RuleFixtures.Rule("ru/same", techniqueMeta: "T1059"));
        var snap = await Collect(_ => Ok(body));
        snap.TotalActiveRules.Should().Be(1);
        snap.Techniques.Single().RuleCount.Should().Be(1);
    }

    [Fact]
    public async Task Coverage_EmptyCompleteCollection_IsAvailableEmpty_NotFailure()
    {
        var snap = await Collect(_ => Ok("{}"));
        snap.State.Should().Be(DetectionCoverageCollectionState.Available);
        snap.TotalActiveRules.Should().Be(0);
        snap.Techniques.Should().BeEmpty();
    }

    [Fact]
    public async Task Coverage_SourceFailure_ReturnsUnavailable_DoesNotThrow()
    {
        var snap = await Collect(_ => (HttpStatusCode.Forbidden, "{}", null));
        snap.State.Should().Be(DetectionCoverageCollectionState.Unavailable, "falha da fonte vira ESTADO, não exceção");
        snap.TotalActiveRules.Should().Be(0);
        snap.Techniques.Should().BeEmpty();
    }

    [Fact]
    public async Task Coverage_PartialCollection_MarksPartial()
    {
        // moreDataAvailable não existe em rules.list; a parcialidade vem do transporte (ciclo de token aqui).
        var snap = await Collect(_ => Ok(RuleFixtures.Page(new[] { RuleFixtures.Rule("ru/1", techniqueMeta: "T1059") }, next: "SAME")));
        snap.State.Should().Be(DetectionCoverageCollectionState.Partial);
        snap.TotalActiveRules.Should().BeGreaterThan(0, "o piso é preservado");
    }

    [Fact]
    public async Task Coverage_YieldsNoScoreSignals()
    {
        var count = 0;
        await foreach (var _ in Connector(_ => Ok("{}")).CollectAsync(Config(), CancellationToken.None)) count++;
        count.Should().Be(0, "cobertura de detecção nunca emite sinais de score");
    }

    [Fact]
    public async Task Coverage_SnapshotCarriesNoServiceAccountSecret()
    {
        var snap = await Collect(_ => Ok(RuleFixtures.List(RuleFixtures.Rule("ru/a", techniqueMeta: "T1059"))));
        JsonSerializer.Serialize(snap).Should().NotContain("service_account").And.NotContain("fake-token");
    }

    private sealed class FakeAuth : IGoogleSecOpsAuthenticator
    {
        public Task<string> AcquireAccessTokenAsync(string serviceAccountJson, CancellationToken ct) => Task.FromResult("fake-token");
    }

    private sealed class PassThrough : IConnectorSecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
