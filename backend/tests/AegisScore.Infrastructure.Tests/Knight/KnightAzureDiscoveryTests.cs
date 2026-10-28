using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AegisScore.Application.Identity.Adm;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Connectors.Microsoft.Knight.Azure;
using AegisScore.Domain;
using AegisScore.Infrastructure.Identity;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COLLECTION-GAPS-01] Descoberta das assinaturas do Azure VERIFICÁVEL, pelo percurso inteiro (coletor real →
/// ADM → releitura → avaliação). O relatório de 27/10 tinha os 213 controles com "nenhuma assinatura no escopo (nenhuma
/// visível, ou todas desabilitadas)" — sem dizer qual. Agora: lista vazia válida, resposta fora do contrato, autorização
/// recusada e filtro de escopo são distinguíveis; as pedidas e não listadas passam pela consulta individual documentada; o
/// token do Resource Manager é conferido (recurso e locatário); nada disso conclui falta do papel Leitor.
/// </summary>
public sealed class KnightAzureDiscoveryTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0606-0606-0606-0000000000a1");
    private const string DirectoryId = "11111111-2222-4333-8444-555555555555";

    private readonly SqliteConnection _connection;
    private readonly ITestOutputHelper _output;

    public KnightAzureDiscoveryTests(ITestOutputHelper output)
    {
        _output = output;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static IReadOnlyList<string> AzureIds =>
        KnightCatalog.Indicators.Where(d => d.Sources.Contains(KnightSourceType.MicrosoftAzure)).Select(d => d.Id).ToList();

    [Fact]
    public async Task ListaVaziaValida_SemEscopoPedido_DizOCaso_ENaoConcluiFaltaDePapel()
    {
        var (run, db) = await RunAsync(new AzureScenario(AzureScenario.Variant.NoSubscriptions), Config(scope: null));

        var discovery = run.Capabilities.Single(c => c.Capability == KnightCapability.AzureSubscriptions);
        discovery.Outcome.Should().Be(KnightCapabilityOutcome.Collected, "a listagem respondeu com o contrato documentado");
        discovery.Detail.Should().Contain("0 assinatura(s) consultada(s) de 0 visível(is)").And.Contain("lista válida e VAZIA")
            .And.NotContain("atribua");

        var summary = await SummaryAsync(db, run.Id);
        summary.Should().BeEquivalentTo(new { Listed = 0, Selected = 0, Disabled = 0, Requested = 0, RequestedNotListed = 0 });

        var own = run.Indicators.Where(i => i.IndicatorId.StartsWith("AK-AZ-", StringComparison.Ordinal)).ToList();
        own.Should().HaveCount(AzureIds.Count).And.OnlyContain(i => i.Status == KnightIndicatorStatus.NotEvaluated);
        own.Where(i => i.IndicatorId is not ("AK-AZ-MON-003" or "AK-AZ-MON-004"))
            .Should().OnlyContain(i => i.NotEvaluatedReason!.Contains("lista válida e VAZIA") && i.NotEvaluatedReason.Contains("não prova"));
        run.Score.Should().BeNull();
    }

    [Fact]
    public async Task ListagemForaDoContrato_EhErroDeContrato_NuncaListaVazia()
    {
        var (run, _) = await RunAsync(new AzureScenario(AzureScenario.Variant.ListingContractBroken), Config(scope: null));

        var discovery = run.Capabilities.Single(c => c.Capability == KnightCapability.AzureSubscriptions);
        discovery.Outcome.Should().Be(KnightCapabilityOutcome.Error);
        discovery.Diagnostic!.Kind.Should().Be(KnightFailureKind.Contract);
        discovery.Detail.Should().Contain("contrato").And.Contain("/subscriptions");
        run.Capabilities.Where(c => c.Capability is not (KnightCapability.AzureSubscriptions)).Should()
            .OnlyContain(c => c.Outcome == KnightCapabilityOutcome.NotAttempted);
        run.Indicators.Where(i => i.IndicatorId.StartsWith("AK-AZ-", StringComparison.Ordinal))
            .Should().OnlyContain(i => i.Status == KnightIndicatorStatus.NotEvaluated && !i.NotEvaluatedReason!.Contains("lista válida"));
    }

    [Fact]
    public async Task AutorizacaoRecusadaNaListagem_EhPermissao_NaoListaVazia()
    {
        var (run, _) = await RunAsync(new AzureScenario(AzureScenario.Variant.DiscoveryDenied), Config(scope: null));
        var discovery = run.Capabilities.Single(c => c.Capability == KnightCapability.AzureSubscriptions);
        discovery.Outcome.Should().Be(KnightCapabilityOutcome.InsufficientPermission);
        discovery.Detail.Should().Contain("HTTP 403").And.Contain("AuthorizationFailed");
    }

    [Fact]
    public async Task TodasDesabilitadas_FiltroDeEscopo_ContadasSeparadamente()
    {
        var (run, db) = await RunAsync(new AzureScenario(AzureScenario.Variant.AllDisabled), Config(scope: null));

        run.Capabilities.Single(c => c.Capability == KnightCapability.AzureSubscriptions).Detail
            .Should().Contain("0 assinatura(s) consultada(s) de 2 visível(is)").And.Contain("2 desabilitada(s)");
        (await SummaryAsync(db, run.Id)).Should().BeEquivalentTo(new { Listed = 2, Selected = 0, Disabled = 2 });
        run.Indicators.Single(i => i.IndicatorId == "AK-AZ-STO-018").NotEvaluatedReason
            .Should().Contain("2 assinatura(s) listada(s) e nenhuma selecionada (2 desabilitada(s))");
    }

    [Fact]
    public async Task PedidaNaoListada_RecusadaNaConsultaIndividual_FicaNoEscopoSemAcesso_ELeituraDeAPreservada()
    {
        var scenario = new AzureScenario(AzureScenario.Variant.RequestedDeniedIndividually);
        var (run, db) = await RunAsync(scenario, Config(scope: new[] { AzureScenario.SubA, AzureScenario.SubB }));

        scenario.Requests.Should().Contain(u => u.Contains($"/subscriptions/{AzureScenario.SubB}?api-version=2022-12-01", StringComparison.Ordinal),
            "a assinatura pedida e não listada passa pela consulta individual documentada");
        var discovery = run.Capabilities.Single(c => c.Capability == KnightCapability.AzureSubscriptions);
        discovery.Detail.Should().Contain("1 assinatura(s) consultada(s) de 2 visível(is)").And.Contain("1 fora do escopo pedido")
            .And.Contain("1 pedida(s) e não listada(s): 1 com autorização recusada (selecionada(s) sem acesso)");

        var summary = await SummaryAsync(db, run.Id);
        summary.Should().BeEquivalentTo(new { Listed = 2, Selected = 1, Requested = 2, RequestedNotListed = 1, RequestedDenied = 1 });
        var inventories = (await Observations(db, run.Id)).Where(o => o.Kind == ConfigurationObjectKind.AzureSubscription)
            .Select(o => JsonSerializer.Deserialize<AzureSubscriptionInventory>(o.ConfigurationJson, IdentityEvidenceFactsJson.Options)!).ToList();
        var b = inventories.Single(i => i.SubscriptionId == AzureScenario.SubB);
        b.InScope.Should().BeTrue("a seleção é preservada");
        b.Visible.Should().BeFalse();
        b.Access.Should().Be(AzureSubscriptionAccess.Denied);
        b.AccessDetail.Should().Contain("HTTP 403").And.Contain("AuthorizationFailed");

        // A falha em B não apaga o que foi lido em A: os recursos de A estão no ADM e uma violação lida continuaria valendo.
        (await Observations(db, run.Id)).Count(o => o.Kind == ConfigurationObjectKind.AzureResource
            && o.ExternalId.Contains(AzureScenario.SubA, StringComparison.OrdinalIgnoreCase)).Should().BePositive();
        var storage = run.Indicators.Single(i => i.IndicatorId == "AK-AZ-STO-018");
        storage.Status.Should().Be(KnightIndicatorStatus.NotEvaluated, "A conforme não aprova um escopo que inclui B sem acesso");
        storage.NotEvaluatedReason.Should().Contain("autorização recusada");
    }

    [Fact]
    public async Task PedidaNaoListada_ConfirmadaNaConsultaIndividual_EhLida()
    {
        var scenario = new AzureScenario(AzureScenario.Variant.RequestedFoundIndividually);
        var (run, db) = await RunAsync(scenario, Config(scope: new[] { AzureScenario.SubA, AzureScenario.SubB }));

        run.Capabilities.Single(c => c.Capability == KnightCapability.AzureSubscriptions).Detail
            .Should().Contain("2 assinatura(s) consultada(s) de 2 visível(is)").And.Contain("1 confirmada(s) pela consulta individual e lida(s)");
        scenario.Requests.Should().Contain(u => u.Contains($"/subscriptions/{AzureScenario.SubB}/providers/Microsoft.Storage/storageAccounts", StringComparison.Ordinal));
        run.Capabilities.Single(c => c.Capability == KnightCapability.AzureStorage).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        run.Indicators.Single(i => i.IndicatorId == "AK-AZ-STO-018").Status.Should().Be(KnightIndicatorStatus.Passed);
        (await SummaryAsync(db, run.Id)).Should().BeEquivalentTo(new { Selected = 2, RequestedDirectRead = 1 });
    }

    [Theory]
    [InlineData(DirectoryId, "locatário configurado.")]
    [InlineData("99999999-2222-4333-8444-555555555555", "locatário DIFERENTE do configurado.")]
    public async Task TokenDoResourceManager_RecursoELocatarioConferidos(string tid, string expected)
    {
        var scenario = new AzureScenario(AzureScenario.Variant.Compliant) { AccessToken = Jwt("https://management.azure.com/", tid) };
        var (run, db) = await RunAsync(scenario, Config(scope: new[] { AzureScenario.SubA, AzureScenario.SubB }));

        run.Capabilities.Single(c => c.Capability == KnightCapability.AzureSubscriptions).Detail
            .Should().Contain("Token do Resource Manager: recurso https://management.azure.com/").And.Contain(expected);
        var json = string.Join("\n", (await Observations(db, run.Id)).Select(o => o.ConfigurationJson));
        json.Should().NotContain(scenario.AccessToken, "o token nunca é gravado — só o recurso e a conferência do locatário");
    }

    [Fact]
    public async Task DiagnosticoDoEntraRecusado_FicaSeparadoDoAcessoAsAssinaturas()
    {
        var (run, _) = await RunAsync(new AzureScenario(AzureScenario.Variant.EntraDiagnosticsDenied),
            Config(scope: new[] { AzureScenario.SubA, AzureScenario.SubB }));

        var diag = run.Capabilities.Single(c => c.Capability == KnightCapability.AzureTenantDiagnostics);
        diag.Outcome.Should().Be(KnightCapabilityOutcome.InsufficientPermission);
        diag.Detail.Should().StartWith(AzureKnightCollector.TenantScopeNote);
        run.Capabilities.Single(c => c.Capability == KnightCapability.AzureSubscriptions).Detail.Should().NotContain("aadiam");
        run.Capabilities.Single(c => c.Capability == KnightCapability.AzureStorage).Outcome.Should().Be(KnightCapabilityOutcome.Collected,
            "a recusa no escopo do locatário não toca as famílias lidas nas assinaturas");
        run.Indicators.Single(i => i.IndicatorId == "AK-AZ-STO-018").Status.Should().Be(KnightIndicatorStatus.Passed);
    }

    [Fact]
    public async Task Integracoes_RecebeOResumoDaDescoberta_EODiagnosticoDoEntraSeparado()
    {
        var (run, _) = await RunAsync(new AzureScenario(AzureScenario.Variant.EntraDiagnosticsDenied), Config(scope: null));
        var scope = AegisScore.Api.Workers.KnightSyncWorker.ScopeSummary(run);
        scope.Should().StartWith("Descoberta concluída: 3 assinatura(s) consultada(s) de 3 visível(is)")
            .And.EndWith("Diagnóstico do Entra ID (escopo do locatário, separado das assinaturas): não lido.");
        AegisScore.Api.Workers.KnightSyncWorker.ScopeSummary(run with { SourceType = KnightSourceType.MicrosoftEntraId })
            .Should().BeNull("só o Azure tem descoberta de escopo");
    }

    [Fact]
    public void DeclaracoesDoToken_TokenIlegivel_NaoVerificavel()
    {
        AzureKnightCollector.ArmTokenClaims("token-sintetico", DirectoryId).Should().Be(((string?)null, (bool?)null));
        AzureKnightCollector.ArmTokenClaims("a.!!!.c", DirectoryId).Should().Be(((string?)null, (bool?)null));
        AzureKnightCollector.ArmTokenClaims(Jwt("https://management.azure.com/", DirectoryId), "dir-demo-0001")
            .Should().Be(("https://management.azure.com/", (bool?)null), "locatário configurado por nome não é comparável ao tid");
    }

    // ---- Infra ---------------------------------------------------------------------------------------------

    internal static string Jwt(string aud, string tid)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("""{"alg":"none"}""")}.{B64(JsonSerializer.Serialize(new { aud, tid }))}.assinatura";
    }

    private static KnightMicrosoftServiceConfiguration Config(string[]? scope) =>
        new(KnightSourceType.MicrosoftAzure, DirectoryId, "client-demo", "segredo-sintetico", null, scope);

    private async Task<(KnightAssessment Run, AegisScoreDbContext Db)> RunAsync(AzureScenario scenario, KnightMicrosoftServiceConfiguration config)
    {
        await SeedAsync();
        var db = NewContext(TenantA);
        var run = await KnightAzureFlowTests.ServiceFor(db, TenantA, scenario, config).RunAssessmentAsync(KnightSourceType.MicrosoftAzure);
        foreach (var c in run.Capabilities) _output.WriteLine($"cap {c.Capability} {c.Outcome} {c.Detail}");
        foreach (var i in run.Indicators.Take(5)) _output.WriteLine($"{i.IndicatorId} {i.Status} {i.NotEvaluatedReason ?? i.Evidence}");
        return (run, db);
    }

    private static async Task<AzureDiscoverySummary> SummaryAsync(AegisScoreDbContext db, Guid runId)
    {
        var doc = (await Observations(db, runId)).Single(o => o.Kind == ConfigurationObjectKind.AzureDiscovery);
        doc.SchemaVersion.Should().Be(AzureDiscoverySummary.SchemaVersion);
        return JsonSerializer.Deserialize<AzureDiscoverySummary>(doc.ConfigurationJson, IdentityEvidenceFactsJson.Options)!;
    }

    private static async Task<List<IdentityConfigurationObservation>> Observations(AegisScoreDbContext db, Guid runId)
    {
        var run = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
        return await db.IdentityConfigurationObservations.AsNoTracking().Where(o => o.AcquisitionId == run.IdentityAcquisitionId).ToListAsync();
    }

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options, new SystemTenantContext(tenantId));

    private async Task SeedAsync()
    {
        await using (var db = NewContext(null))
        {
            if (await db.Tenants.AnyAsync(t => t.Id == TenantA)) return;
            db.Tenants.Add(new Tenant { Id = TenantA, Name = "Cliente Demo", Slug = $"t-{TenantA:N}", Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using var dbt = NewContext(TenantA);
        dbt.Connectors.Add(new ConnectorConfig
        {
            TenantId = TenantA, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.IdentityPosture,
            DisplayName = "Microsoft 365", Enabled = true, EncryptedSettings = "cifrado",
        });
        await dbt.SaveChangesAsync();
    }
}
