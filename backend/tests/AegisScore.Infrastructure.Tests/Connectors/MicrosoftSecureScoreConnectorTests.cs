using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Connectors.Microsoft;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Connectors;

/// <summary>
/// [AEGIS-MVP-POSTURE-02] Coletor REAL do Microsoft Secure Score por HTTP SIMULADO (sem rede, sem credenciais
/// reais): exercita o protocolo verdadeiro nas URLs OFICIAIS (login.microsoftonline.com / graph.microsoft.com)
/// via o transporte VALIDADO do KNIGHT (<see cref="EntraGraphClient"/>). Cobre: OAuth + leitura; overall e
/// categorias; categoria incompleta não emitida; mismatch de azureTenantId fail-closed; 401/403/429/5xx e JSON
/// inválido sanitizados; paginação e rejeição de nextLink fora do host; exposições (findings) + deprecated;
/// e a ausência de qualquer caminho SampleScores/Demo/Stub.
/// </summary>
public sealed class MicrosoftSecureScoreConnectorTests
{
    private const string TenantId = "11111111-2222-3333-4444-555555555555";
    private const string ClientSecret = "SUPER-SECRET-VALUE";
    private const string TokenJson = """{"access_token":"fake-access-token","expires_in":3600,"token_type":"Bearer"}""";

    private static ConnectorConfig Config() => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.Parse("aa000000-0000-0000-0000-000000000001"),
        Provider = ConnectorProvider.Microsoft,
        Capability = ConnectorCapability.SecureScore,
        Enabled = true,
        EncryptedSettings =
            $$"""{"tenantId":"{{TenantId}}","clientId":"app-client-id","clientSecret":"{{ClientSecret}}"}""",
    };

    private static MicrosoftSecureScoreConnector NewConnector(HttpMessageHandler handler) =>
        new(new EntraGraphClient(new HttpClient(handler)), new IdentityConnectorSecretProtector());

    private static async Task<List<EvidenceSignal>> CollectSignalsAsync(MicrosoftSecureScoreConnector c, ConnectorConfig cfg)
    {
        var list = new List<EvidenceSignal>();
        await foreach (var s in c.CollectAsync(cfg, CancellationToken.None))
            list.Add(s);
        return list;
    }

    // ---- 1) OAuth + chamada Graph real simulada: overall + categorias ------------------------------

    [Fact]
    public async Task Collect_HappyPath_ProducesOverallAndCategorySignals()
    {
        var connector = NewConnector(HappyHandler());
        var signals = await CollectSignalsAsync(connector, Config());

        var byKey = signals.ToDictionary(s => s.SignalKey, s => s.NumericValue);
        byKey.Should().ContainKey("secureScore.overall");
        byKey["secureScore.overall"].Should().Be(54, "currentScore 54 / maxScore 100 × 100");
        byKey["secureScore.identity"].Should().Be(40, "Identity: (5+3) / (10+10) × 100");
        byKey["secureScore.data"].Should().Be(70, "Data: 7 / 10 × 100");
        byKey["secureScore.device"].Should().Be(20, "Device: 2 / 10 × 100");
        byKey["secureScore.apps"].Should().Be(40, "Apps: 4 / 10 × 100");

        signals.Should().OnlyContain(s => s.Unit == "percent", "todo sinal do Secure Score é percentual");
        // O adaptador NÃO é autoridade de mapping — deixa os códigos vazios (o executor re-mapeia).
        signals.Should().OnlyContain(s => s.MappedSubcategoryCodes.Count == 0);
        // O instante de coleta é o createdDateTime da fotografia.
        signals.Should().OnlyContain(s => s.CollectedAt == DateTimeOffset.Parse("2026-08-20T10:00:00Z"));
    }

    // ---- 2) Ausência de qualquer uso de SampleScores/Demo/Stub -------------------------------------

    [Fact]
    public void Connector_HasNoSampleOrDemoOrStubMethod()
    {
        var members = typeof(MicrosoftSecureScoreConnector)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Select(m => m.Name);
        members.Should().NotContain(n =>
            n.Contains("Sample", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Demo", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Stub", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Collect_GraphFailure_ThrowsAndYieldsNoRepresentativeValues()
    {
        // Graph 500 na leitura da fotografia → EntraGraphException; NUNCA cai para valores representativos.
        var handler = new StubHandler(req =>
            IsToken(req) ? (HttpStatusCode.OK, TokenJson) : (HttpStatusCode.InternalServerError, "{}"));
        var connector = NewConnector(handler);

        var act = async () => await CollectSignalsAsync(connector, Config());
        await act.Should().ThrowAsync<EntraGraphException>();
    }

    // ---- 4/5) ControlScore sem perfil → FALHA FECHADA (não é seguro reconciliar com correspondência incompleta) --

    [Fact]
    public async Task Collect_ControlScoreWithoutProfile_FailsClosed()
    {
        // Um controlScore ("c-id-3") SEM perfil correspondente NÃO é uma coleta parcialmente "saudável": a
        // correspondência incompleta invalida a fotografia inteira (não se resolve/reconcilia por omissão).
        var score = ScorePayload(current: 54, max: 100, controls: DefaultControlScores()
            .Append(("c-id-3", "Identity", 1)).ToArray());
        var act = async () => await CollectSignalsAsync(NewConnector(HandlerWith(score, DefaultProfiles())), Config());
        (await act.Should().ThrowAsync<EntraGraphException>()).Which.Kind.Should().Be(EntraGraphErrorKind.Unavailable);
    }

    // ---- 6) Mismatch de azureTenantId falha fechado -----------------------------------------------

    [Fact]
    public async Task Collect_AzureTenantIdMismatch_FailsClosed()
    {
        var score = ScorePayload(current: 54, max: 100, controls: DefaultControlScores(), azureTenantId: "99999999-0000-0000-0000-000000000000");
        var handler = HandlerWith(score, DefaultProfiles());
        var connector = NewConnector(handler);

        var act = async () => await CollectSignalsAsync(connector, Config());
        (await act.Should().ThrowAsync<EntraGraphException>())
            .Which.Kind.Should().Be(EntraGraphErrorKind.AuthFailure);
    }

    // ---- 7) 401/403/429/5xx e JSON inválido sanitizados -------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, EntraGraphErrorKind.AuthFailure)]
    [InlineData(HttpStatusCode.Forbidden, EntraGraphErrorKind.InsufficientPermission)]
    [InlineData(HttpStatusCode.TooManyRequests, EntraGraphErrorKind.Throttled)]
    [InlineData(HttpStatusCode.InternalServerError, EntraGraphErrorKind.Unavailable)]
    public async Task Collect_GraphErrorStatuses_ClassifiedAndSanitized(HttpStatusCode status, EntraGraphErrorKind kind)
    {
        var handler = new StubHandler(req =>
            IsToken(req) ? (HttpStatusCode.OK, TokenJson) : (status, """{"error":{"code":"x"}}"""));
        var connector = NewConnector(handler);

        var act = async () => await CollectSignalsAsync(connector, Config());
        var ex = (await act.Should().ThrowAsync<EntraGraphException>()).Which;
        ex.Kind.Should().Be(kind);
        (ex.Message ?? "").Should().NotContain(ClientSecret, "a exceção nunca carrega o segredo do cliente");
        (ex.Message ?? "").Should().NotContain("fake-access-token", "a exceção nunca carrega o bearer");
    }

    [Fact]
    public async Task Collect_InvalidTokenJson_FailsClosedSanitized()
    {
        var handler = new StubHandler(req => IsToken(req)
            ? (HttpStatusCode.OK, "not-json-at-all")
            : (HttpStatusCode.OK, "{}"));
        var connector = NewConnector(handler);

        var act = async () => await CollectSignalsAsync(connector, Config());
        var ex = (await act.Should().ThrowAsync<EntraGraphException>()).Which;
        ex.Kind.Should().Be(EntraGraphErrorKind.AuthFailure);
        (ex.Message ?? "").Should().NotContain(ClientSecret);
    }

    // ---- 8) Paginação e rejeição de nextLink fora do host oficial ---------------------------------

    [Fact]
    public async Task CollectFindings_FollowsProfilePagination()
    {
        // Perfis em DUAS páginas via @odata.nextLink oficial — o coletor precisa seguir a paginação.
        var page2 = """{"value":[{"id":"c-data-1","title":"D1","controlCategory":"Data","service":"Exchange","maxScore":10,"rank":9,"tier":"Core","threats":[]}]}""";
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (IsToken(req)) return (HttpStatusCode.OK, TokenJson);
            // Só os controles que têm perfil nas duas páginas — cada controlScore precisa de um perfil correspondente.
            if (url.Contains("secureScores")) return (HttpStatusCode.OK, ScorePayload(54, 100, new[] { ("c-id-1", "Identity", 5.0), ("c-data-1", "Data", 7.0) }));
            if (url.Contains("page=2")) return (HttpStatusCode.OK, page2);
            if (url.Contains("secureScoreControlProfiles"))
                return (HttpStatusCode.OK, """{"value":[{"id":"c-id-1","title":"I1","controlCategory":"Identity","service":"AAD","maxScore":10,"rank":1,"tier":"Core","threats":[]}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/security/secureScoreControlProfiles?page=2"}""");
            return (HttpStatusCode.NotFound, "{}");
        });
        var connector = NewConnector(handler);

        var result = await connector.CollectFindingsAsync(Config(), CancellationToken.None);
        result.IsComplete.Should().BeTrue();
        result.Findings.Select(f => f.ExternalId).Should().Contain(new[] { "c-id-1", "c-data-1" },
            "os perfis das DUAS páginas participam");
    }

    [Fact]
    public async Task CollectFindings_MaliciousNextLink_Rejected_NoBearerLeak()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            // Se QUALQUER requisição chegar ao host malicioso, o teste falha (o transporte deve reprová-la antes).
            url.Should().NotContain("evil.example.com", "o bearer nunca pode sair para um host fora da allowlist");
            if (IsToken(req)) return (HttpStatusCode.OK, TokenJson);
            if (url.Contains("secureScores")) return (HttpStatusCode.OK, ScorePayload(54, 100, DefaultControlScores()));
            if (url.Contains("secureScoreControlProfiles"))
                return (HttpStatusCode.OK, """{"value":[{"id":"c-id-1","maxScore":10}],"@odata.nextLink":"https://evil.example.com/steal?t=1"}""");
            return (HttpStatusCode.NotFound, "{}");
        });
        var connector = NewConnector(handler);

        var act = async () => await connector.CollectFindingsAsync(Config(), CancellationToken.None);
        (await act.Should().ThrowAsync<EntraGraphException>())
            .Which.Kind.Should().Be(EntraGraphErrorKind.Unavailable);
    }

    // ---- Exposições: gap positivo, deprecated ignorado, estado da fonte capturado -----------------

    [Fact]
    public async Task CollectFindings_BuildsFindings_SkipsDeprecated_CapturesSourceState()
    {
        var connector = NewConnector(HappyHandler());
        var result = await connector.CollectFindingsAsync(Config(), CancellationToken.None);

        result.IsComplete.Should().BeTrue();
        result.SourceLabel.Should().Be("Microsoft Secure Score");
        result.Findings.Should().OnlyContain(f => f.Gap > 0, "só gap positivo vira exposição aberta");
        result.Findings.Should().NotContain(f => f.ExternalId == "c-dep-1", "controle deprecated não cria exposição");

        var identity = result.Findings.Single(f => f.ExternalId == "c-id-1");
        identity.MaxScore.Should().Be(10);
        identity.CurrentScore.Should().Be(5);
        identity.Gap.Should().Be(5);
        identity.Category.Should().Be("Identity");
        identity.Service.Should().Be("Azure Active Directory");
        identity.SourceRank.Should().Be(1);
        identity.Threats.Should().Contain("Account Breach");
        identity.SourceState.Should().Be("Ignored", "o estado da fonte é capturado como metadado");
    }

    [Fact]
    public async Task CollectFindings_NoSnapshot_NotCompleteNoFindings()
    {
        // Sem fotografia (value vazio) → nada a reconciliar; coleta NÃO completa (não resolve por omissão).
        var handler = new StubHandler(req =>
        {
            if (IsToken(req)) return (HttpStatusCode.OK, TokenJson);
            if (req.RequestUri!.AbsoluteUri.Contains("secureScores")) return (HttpStatusCode.OK, """{"value":[]}""");
            return (HttpStatusCode.OK, """{"value":[]}""");
        });
        var connector = NewConnector(handler);

        var result = await connector.CollectFindingsAsync(Config(), CancellationToken.None);
        result.IsComplete.Should().BeFalse();
        result.Findings.Should().BeEmpty();
    }

    // ---- 2) Completude realmente fail-closed (206, JSON inválido, estrutura/dados inconsistentes) --
    // Cada caso deve LANÇAR EntraGraphException sanitizada (o executor carimba Failed e não resolve por omissão).
    // Nenhum vira IsComplete=true, 0%, 100%, conformidade ou resolução.

    [Fact]
    public async Task CollectFindings_PartialContent206_FailsClosed()
    {
        // 206 é 2xx mas NÃO é resposta completa — recusado como fonte incompleta/indisponível.
        var handler = new StubHandler(req => IsToken(req)
            ? (HttpStatusCode.OK, TokenJson)
            : (HttpStatusCode.PartialContent, ScorePayload(54, 100, DefaultControlScores())));
        var act = async () => await NewConnector(handler).CollectFindingsAsync(Config(), CancellationToken.None);
        (await act.Should().ThrowAsync<EntraGraphException>()).Which.Kind.Should().Be(EntraGraphErrorKind.Unavailable);
    }

    [Fact]
    public async Task CollectFindings_InvalidJsonInSecureScores_FailsClosedSanitized()
    {
        var handler = new StubHandler(req => IsToken(req)
            ? (HttpStatusCode.OK, TokenJson)
            : (HttpStatusCode.OK, "not-json-at-all"));
        var act = async () => await NewConnector(handler).CollectFindingsAsync(Config(), CancellationToken.None);
        var ex = (await act.Should().ThrowAsync<EntraGraphException>()).Which;
        ex.Kind.Should().Be(EntraGraphErrorKind.Unavailable);
        (ex.Message ?? "").Should().NotContain(ClientSecret);
        (ex.Message ?? "").Should().NotContain("fake-access-token");
    }

    [Fact]
    public async Task CollectFindings_InvalidJsonInProfilePage_FailsClosed()
    {
        var handler = new StubHandler(req =>
        {
            if (IsToken(req)) return (HttpStatusCode.OK, TokenJson);
            if (req.RequestUri!.AbsoluteUri.Contains("secureScores"))
                return (HttpStatusCode.OK, ScorePayload(54, 100, DefaultControlScores()));
            return (HttpStatusCode.OK, "not-json");   // página de perfis com JSON inválido
        });
        var act = async () => await NewConnector(handler).CollectFindingsAsync(Config(), CancellationToken.None);
        (await act.Should().ThrowAsync<EntraGraphException>()).Which.Kind.Should().Be(EntraGraphErrorKind.Unavailable);
    }

    [Fact]
    public async Task CollectFindings_ControlScoresMissing_FailsClosed()
    {
        var score = $$"""{"value":[{"azureTenantId":"{{TenantId}}","currentScore":54,"maxScore":100,"createdDateTime":"2026-08-20T10:00:00Z"}]}""";
        var act = async () => await NewConnector(HandlerWith(score, DefaultProfiles())).CollectFindingsAsync(Config(), CancellationToken.None);
        (await act.Should().ThrowAsync<EntraGraphException>()).Which.Kind.Should().Be(EntraGraphErrorKind.Unavailable);
    }

    [Fact]
    public async Task CollectFindings_DuplicateControlName_FailsClosed()
    {
        var score = ScorePayload(54, 100, new[] { ("c-id-1", "Identity", 5.0), ("c-id-1", "Identity", 4.0) });
        var act = async () => await NewConnector(HandlerWith(score, DefaultProfiles())).CollectFindingsAsync(Config(), CancellationToken.None);
        (await act.Should().ThrowAsync<EntraGraphException>()).Which.Kind.Should().Be(EntraGraphErrorKind.Unavailable);
    }

    [Fact]
    public async Task CollectFindings_DuplicateProfileId_FailsClosed()
    {
        var score = ScorePayload(54, 100, new[] { ("c-id-1", "Identity", 5.0) });
        var profiles = """{"value":[{"id":"c-id-1","controlCategory":"Identity","maxScore":10,"threats":[]},{"id":"c-id-1","controlCategory":"Identity","maxScore":10,"threats":[]}]}""";
        var act = async () => await NewConnector(HandlerWith(score, profiles)).CollectFindingsAsync(Config(), CancellationToken.None);
        (await act.Should().ThrowAsync<EntraGraphException>()).Which.Kind.Should().Be(EntraGraphErrorKind.Unavailable);
    }

    [Fact]
    public async Task CollectFindings_ScoreAboveMax_FailsClosed()
    {
        var score = ScorePayload(54, 100, new[] { ("c-id-1", "Identity", 15.0) });   // 15 > maxScore 10
        var act = async () => await NewConnector(HandlerWith(score, DefaultProfiles())).CollectFindingsAsync(Config(), CancellationToken.None);
        (await act.Should().ThrowAsync<EntraGraphException>()).Which.Kind.Should().Be(EntraGraphErrorKind.Unavailable);
    }

    [Fact]
    public async Task CollectFindings_NegativeScore_FailsClosed()
    {
        var score = ScorePayload(54, 100, new[] { ("c-id-1", "Identity", -1.0) });
        var act = async () => await NewConnector(HandlerWith(score, DefaultProfiles())).CollectFindingsAsync(Config(), CancellationToken.None);
        (await act.Should().ThrowAsync<EntraGraphException>()).Which.Kind.Should().Be(EntraGraphErrorKind.Unavailable);
    }

    // ---- 2b) Validação INTEGRAL da fotografia (perfil, correspondência, categoria, score geral, data) ---------

    // Controle válido reutilizado quando a falha esperada NÃO está no controlScore (perfil c-id-1 é Identity/max 10).
    private static object[] ValidControl => new object[] { new { controlName = "c-id-1", controlCategory = "Identity", score = 5 } };

    private static string ScoreDoc(object score) => JsonSerializer.Serialize(new { value = new[] { score } });

    private async Task AssertFindingsFailClosedAsync(string scoreJson, string? profilesJson = null)
    {
        var handler = HandlerWith(scoreJson, profilesJson ?? DefaultProfiles());
        var act = async () => await NewConnector(handler).CollectFindingsAsync(Config(), CancellationToken.None);
        (await act.Should().ThrowAsync<EntraGraphException>()).Which.Kind.Should().Be(EntraGraphErrorKind.Unavailable);
    }

    [Fact]
    public Task Collect_ProfileWithEmptyId_FailsClosed()
    {
        var profiles = JsonSerializer.Serialize(new
        {
            value = new object[]
            {
                new { id = "", controlCategory = "Identity", maxScore = 10 },
                new { id = "c-id-1", controlCategory = "Identity", maxScore = 10 },
            },
        });
        return AssertFindingsFailClosedAsync(
            ScoreDoc(new { azureTenantId = TenantId, currentScore = 54, maxScore = 100, createdDateTime = "2026-08-20T10:00:00Z", controlScores = ValidControl }),
            profiles);
    }

    [Fact]
    public Task Collect_ControlScoresEmpty_FailsClosed() =>
        AssertFindingsFailClosedAsync(ScoreDoc(new
        {
            azureTenantId = TenantId, currentScore = 54, maxScore = 100, createdDateTime = "2026-08-20T10:00:00Z",
            controlScores = Array.Empty<object>(),
        }));

    [Fact]
    public Task Collect_ControlCategoryEmpty_FailsClosed() =>
        AssertFindingsFailClosedAsync(ScoreDoc(new
        {
            azureTenantId = TenantId, currentScore = 54, maxScore = 100, createdDateTime = "2026-08-20T10:00:00Z",
            controlScores = new object[] { new { controlName = "c-id-1", controlCategory = "", score = 5 } },
        }));

    [Fact]
    public Task Collect_CategoryMismatchBetweenScoreAndProfile_FailsClosed() =>
        // controlScore diz "Data"; o perfil c-id-1 é "Identity" — divergência (mais que caixa) → falha fechada.
        AssertFindingsFailClosedAsync(ScoreDoc(new
        {
            azureTenantId = TenantId, currentScore = 54, maxScore = 100, createdDateTime = "2026-08-20T10:00:00Z",
            controlScores = new object[] { new { controlName = "c-id-1", controlCategory = "Data", score = 5 } },
        }));

    [Fact]
    public Task Collect_OverallCurrentMissing_FailsClosed() =>
        AssertFindingsFailClosedAsync(ScoreDoc(new
        {
            azureTenantId = TenantId, maxScore = 100, createdDateTime = "2026-08-20T10:00:00Z", controlScores = ValidControl,
        }));

    [Fact]
    public Task Collect_OverallCurrentNegative_FailsClosed() =>
        AssertFindingsFailClosedAsync(ScoreDoc(new
        {
            azureTenantId = TenantId, currentScore = -1, maxScore = 100, createdDateTime = "2026-08-20T10:00:00Z", controlScores = ValidControl,
        }));

    [Fact]
    public Task Collect_OverallCurrentAboveMax_FailsClosed() =>
        AssertFindingsFailClosedAsync(ScoreDoc(new
        {
            azureTenantId = TenantId, currentScore = 150, maxScore = 100, createdDateTime = "2026-08-20T10:00:00Z", controlScores = ValidControl,
        }));

    [Fact]
    public Task Collect_OverallMaxMissing_FailsClosed() =>
        AssertFindingsFailClosedAsync(ScoreDoc(new
        {
            azureTenantId = TenantId, currentScore = 54, createdDateTime = "2026-08-20T10:00:00Z", controlScores = ValidControl,
        }));

    [Fact]
    public Task Collect_OverallMaxZero_FailsClosed() =>
        AssertFindingsFailClosedAsync(ScoreDoc(new
        {
            azureTenantId = TenantId, currentScore = 54, maxScore = 0, createdDateTime = "2026-08-20T10:00:00Z", controlScores = ValidControl,
        }));

    [Fact]
    public Task Collect_OverallMaxNonNumeric_FailsClosed() =>
        AssertFindingsFailClosedAsync(ScoreDoc(new
        {
            azureTenantId = TenantId, currentScore = 54, maxScore = "abc", createdDateTime = "2026-08-20T10:00:00Z", controlScores = ValidControl,
        }));

    [Fact]
    public Task Collect_CreatedDateTimeMissing_FailsClosed() =>
        AssertFindingsFailClosedAsync(ScoreDoc(new
        {
            azureTenantId = TenantId, currentScore = 54, maxScore = 100, controlScores = ValidControl,
        }));

    [Fact]
    public Task Collect_CreatedDateTimeInvalid_FailsClosed() =>
        AssertFindingsFailClosedAsync(ScoreDoc(new
        {
            azureTenantId = TenantId, currentScore = 54, maxScore = 100, createdDateTime = "not-a-date", controlScores = ValidControl,
        }));

    // ---- TestAsync: autenticação + leitura real ($top=1) ------------------------------------------

    [Fact]
    public async Task Test_Healthy_OnValidAuthAndRead()
    {
        var health = await NewConnector(HappyHandler()).TestAsync(Config(), CancellationToken.None);
        health.Status.Should().Be(ConnectorStatus.Healthy);
    }

    [Fact]
    public async Task Test_Failed_OnForbidden()
    {
        var handler = new StubHandler(req =>
            IsToken(req) ? (HttpStatusCode.OK, TokenJson) : (HttpStatusCode.Forbidden, "{}"));
        var health = await NewConnector(handler).TestAsync(Config(), CancellationToken.None);
        health.Status.Should().Be(ConnectorStatus.Failed);
    }

    [Fact]
    public async Task Test_Degraded_WhenNotConfigured()
    {
        var cfg = Config();
        cfg.EncryptedSettings = "";   // sem credenciais
        var health = await NewConnector(HappyHandler()).TestAsync(cfg, CancellationToken.None);
        health.Status.Should().Be(ConnectorStatus.Degraded);
    }

    // ---- Payload builders --------------------------------------------------------------------------

    private static bool IsToken(HttpRequestMessage req) =>
        req.Method == HttpMethod.Post && req.RequestUri!.AbsoluteUri.Contains("/oauth2/v2.0/token");

    private static (string Name, string Category, double Score)[] DefaultControlScores() => new[]
    {
        ("c-id-1", "Identity", 5.0),
        ("c-id-2", "Identity", 3.0),
        ("c-data-1", "Data", 7.0),
        ("c-dev-1", "Device", 2.0),
        ("c-apps-1", "Apps", 4.0),
    };

    private static string ScorePayload(
        double current, double max, (string Name, string Category, double Score)[] controls, string? azureTenantId = null)
    {
        var cs = string.Join(",", controls.Select(c =>
            $$"""{"controlName":"{{c.Name}}","controlCategory":"{{c.Category}}","score":{{c.Score.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"""));
        var tenant = azureTenantId ?? TenantId;
        return $$"""{"value":[{"azureTenantId":"{{tenant}}","currentScore":{{current}},"maxScore":{{max}},"createdDateTime":"2026-08-20T10:00:00Z","controlScores":[{{cs}}]}]}""";
    }

    /// <summary>Perfis padrão: um por controlScore + um DEPRECATED (c-dep-1, com controlScore e gap) que NÃO deve virar exposição.</summary>
    private static string DefaultProfiles()
    {
        static string Profile(string id, string cat, string svc, int rank, string? state = null, bool deprecated = false)
        {
            var su = state is null ? "[]" : $$"""[{"state":"{{state}}","updatedBy":"admin@example.com","comment":"x"}]""";
            return $$"""{"id":"{{id}}","title":"{{id}} title","controlCategory":"{{cat}}","service":"{{svc}}","maxScore":10,"rank":{{rank}},"tier":"Core","implementationCost":"Low","userImpact":"Low","actionType":"Config","remediation":"do it","remediationImpact":"none","threats":["Account Breach"],"deprecated":{{(deprecated ? "true" : "false")}},"controlStateUpdates":{{su}}}""";
        }

        var profiles = new[]
        {
            Profile("c-id-1", "Identity", "Azure Active Directory", 1, state: "Ignored"),
            Profile("c-id-2", "Identity", "Azure Active Directory", 2),
            Profile("c-data-1", "Data", "Exchange Online", 3),
            Profile("c-dev-1", "Device", "Intune", 4),
            Profile("c-apps-1", "Apps", "Microsoft 365 Apps", 5),
            Profile("c-dep-1", "Identity", "Legacy", 6, deprecated: true),
        };
        return $$"""{"value":[{{string.Join(",", profiles)}}]}""";
    }

    private static StubHandler HappyHandler()
    {
        // Fotografia com um controlScore para o controle DEPRECATED também (para provar que ele é ignorado).
        var controls = DefaultControlScores().Append(("c-dep-1", "Identity", 1.0)).ToArray();
        return HandlerWith(ScorePayload(54, 100, controls), DefaultProfiles());
    }

    private static StubHandler HandlerWith(string scoreJson, string profilesJson) => new(req =>
    {
        if (IsToken(req)) return (HttpStatusCode.OK, TokenJson);
        var url = req.RequestUri!.AbsoluteUri;
        if (url.Contains("secureScores")) return (HttpStatusCode.OK, scoreJson);
        if (url.Contains("secureScoreControlProfiles")) return (HttpStatusCode.OK, profilesJson);
        return (HttpStatusCode.NotFound, "{}");
    });

    // ---- Test doubles ------------------------------------------------------------------------------

    /// <summary>Protetor de segredo IDENTIDADE: o "cifrado" é o próprio JSON — testável sem key ring.</summary>
    private sealed class IdentityConnectorSecretProtector : IConnectorSecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    /// <summary>HttpMessageHandler simulado: roteia por requisição (mesmo idioma dos testes do KNIGHT).</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode, string)> _route;
        public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> route) => _route = route;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, body) = _route(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
