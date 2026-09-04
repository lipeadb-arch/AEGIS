using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Identity;
using AegisScore.Application.Knight;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-MVP-MICROSOFT-COVERAGE-03] Testes do RISCO DE IDENTIDADE (Microsoft Entra ID Protection) por HTTP
/// SIMULADO — sem rede e sem credencial real, exercitando o protocolo verdadeiro nas URLs oficiais.
///
/// O que estes testes travam:
///  • TRANSPORTE: um token por aquisição, paginação completa, URL/$select corretos, nextLink só do host
///    oficial, teto de páginas, cancelamento × timeout, 401/403/404/429/5xx, JSON malformado e falha de
///    página INTERMEDIÁRIA (que preserva o parcial em vez de zerar);
///  • SEMÂNTICA: níveis, situações em aberto × resolvidas, bucket desconhecido preservado, usuário excluído
///    fora dos KPIs ativos, janela temporal determinística e zero real só após coleta completa;
///  • INDEPENDÊNCIA: uma capacidade em 403/licença/429 NÃO invalida a outra;
///  • PRIVACIDADE: nenhum identificador, IP, localização, requestId, correlationId, additionalInfo, user
///    agent, token ou segredo atravessa a normalização — nem mesmo quando o Graph os devolve;
///  • DECISÃO: nenhuma chamada individual a /users/{id}/authentication/methods.
/// </summary>
public sealed class EntraIdentityRiskCollectorTests
{
    private static readonly KnightEntraIdConfiguration Cfg = new(
        AzureTenantId: "11111111-2222-3333-4444-555555555555",
        ClientId: "app-client-id",
        ClientSecret: "SUPER-SECRET-VALUE");

    private static readonly Guid Tenant = Guid.Parse("aa000000-0000-0000-0000-000000000001");

    /// <summary>Relógio FIXO: a janela de 30/7 dias é a mesma em toda execução — nada de "agora" implícito.</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private const string TokenJson = """{"access_token":"fake-access-token","expires_in":3600,"token_type":"Bearer"}""";

    private static string Iso(DateTimeOffset value) => value.ToString("o", CultureInfo.InvariantCulture);

    // ================================================================================================
    //  1) TRANSPORTE
    // ================================================================================================

    [Fact]
    public async Task Transport_AcquiresTokenOnce_ForBothRiskCapabilities()
    {
        var handler = new RecordingHandler(Route);
        var result = await CollectAsync(handler);

        handler.TokenRequests.Should().Be(1, "uma operação lógica adquire UM token, reaproveitado por tudo");
        result.IdentityRisk!.RiskyUsersOutcome.Should().Be(KnightCapabilityOutcome.Collected);
        result.IdentityRisk.RiskDetectionsOutcome.Should().Be(KnightCapabilityOutcome.Collected);
    }

    [Fact]
    public async Task Transport_RequestsOfficialUrls_WithPrivacyPreservingSelect()
    {
        var handler = new RecordingHandler(Route);
        await CollectAsync(handler);

        var risky = handler.Urls.Single(u => u.Contains("identityProtection/riskyUsers", StringComparison.Ordinal));
        var detections = handler.Urls.Single(u => u.Contains("identityProtection/riskDetections", StringComparison.Ordinal));

        risky.Should().StartWith("https://graph.microsoft.com/v1.0/identityProtection/riskyUsers");
        detections.Should().StartWith("https://graph.microsoft.com/v1.0/identityProtection/riskDetections");

        // $top=500 é o MÁXIMO documentado por página nos dois recursos.
        risky.Should().Contain("$top=500");
        detections.Should().Contain("$top=500");

        // O $select pede o que é agregável...
        risky.Should().Contain("riskLevel").And.Contain("riskState").And.Contain("isDeleted");
        detections.Should().Contain("riskEventType").And.Contain("detectedDateTime").And.Contain("detectionTimingType");

        // ...e NUNCA o que é pessoal: minimização já na origem, o PII sequer trafega.
        foreach (var url in new[] { risky, detections })
            foreach (var forbidden in new[]
                     { "userDisplayName", "userPrincipalName", "userId", "ipAddress", "location", "requestId", "correlationId", "additionalInfo" })
                url.Should().NotContain(forbidden, $"{forbidden} não pode ser solicitado ao Graph");
    }

    [Fact]
    public async Task Transport_FollowsPagination_AcrossAllPages()
    {
        var handler = new RecordingHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("riskyUsers", StringComparison.Ordinal))
            {
                if (url.Contains("page=2", StringComparison.Ordinal))
                    return Ok("""{"value":[{"riskLevel":"low","riskState":"atRisk"}]}""");
                return Ok("""
                    {"value":[{"riskLevel":"high","riskState":"atRisk"}],
                     "@odata.nextLink":"https://graph.microsoft.com/v1.0/identityProtection/riskyUsers?page=2"}
                    """);
            }
            return Route(req);
        });

        var result = await CollectAsync(handler);

        result.IdentityRisk!.RiskyUsers!.Total.Should().Be(2, "as DUAS páginas foram consumidas");
        result.IdentityRisk.RiskyUsers.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task Transport_RejectsNextLinkFromForeignHost_BearerNeverLeavesOfficialHost()
    {
        var handler = new RecordingHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("riskyUsers", StringComparison.Ordinal))
                return Ok("""
                    {"value":[{"riskLevel":"high","riskState":"atRisk"}],
                     "@odata.nextLink":"https://evil.example.com/v1.0/identityProtection/riskyUsers?page=2"}
                    """);
            return Route(req);
        });

        var result = await CollectAsync(handler);

        handler.Urls.Should().OnlyContain(u => u.StartsWith("https://graph.microsoft.com", StringComparison.Ordinal)
                                            || u.StartsWith("https://login.microsoftonline.com", StringComparison.Ordinal));
        // A capacidade degrada, mas o que já foi lido é PRESERVADO — nunca zero.
        result.IdentityRisk!.RiskyUsersOutcome.Should().Be(KnightCapabilityOutcome.Unavailable);
        result.IdentityRisk.RiskyUsers!.Total.Should().Be(1);
        result.IdentityRisk.RiskyUsers.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task Transport_RedirectResponseIsNotFollowed_AuthorizationStaysOnOfficialHost()
    {
        var handler = new RecordingHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("riskDetections", StringComparison.Ordinal))
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Redirect) { Content = new StringContent("", Encoding.UTF8, "application/json") };
                resp.Headers.Location = new Uri("https://evil.example.com/steal");
                return resp;
            }
            return Route(req);
        });

        var result = await CollectAsync(handler);

        handler.Urls.Should().NotContain(u => u.Contains("evil.example.com", StringComparison.Ordinal));
        handler.AuthorizedHosts.Should().OnlyContain(h => h == "graph.microsoft.com");
        result.IdentityRisk!.RiskDetectionsOutcome.Should().Be(KnightCapabilityOutcome.Unavailable);
    }

    [Fact]
    public async Task Transport_PageCeiling_DoesNotTruncateSilently()
    {
        // nextLink infinito: o teto de páginas do transporte interrompe a leitura...
        var handler = new RecordingHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("riskyUsers", StringComparison.Ordinal))
                return Ok("""
                    {"value":[{"riskLevel":"low","riskState":"atRisk"}],
                     "@odata.nextLink":"https://graph.microsoft.com/v1.0/identityProtection/riskyUsers?page=n"}
                    """);
            return Route(req);
        });

        var collector = new EntraIdKnightCollector(
            new EntraGraphClient(new HttpClient(handler), maxPages: 3), null, new FixedTime(Now));
        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        // ...e o resultado diz isso em vez de apresentar um total truncado como verdade.
        var facts = result.IdentityRisk!.RiskyUsers!;
        facts.Total.Should().Be(3);
        facts.IsComplete.Should().BeFalse("uma leitura interrompida jamais é apresentada como total");
        result.IdentityRisk.RiskyUsersOutcome.Should().Be(KnightCapabilityOutcome.Unavailable);
    }

    [Fact]
    public async Task Transport_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var collector = Collector(new RecordingHandler(Route));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg), cts.Token));
    }

    [Fact]
    public async Task Transport_Timeout_DegradesOnlyThatCapability()
    {
        var handler = new RecordingHandler(req =>
        {
            // TaskCanceledException SEM cancelamento do chamador = timeout do HttpClient.
            if (req.RequestUri!.AbsoluteUri.Contains("riskDetections", StringComparison.Ordinal))
                throw new TaskCanceledException("timeout simulado");
            return Route(req);
        });

        var result = await CollectAsync(handler);

        result.IdentityRisk!.RiskDetectionsOutcome.Should().Be(KnightCapabilityOutcome.Unavailable);
        result.IdentityRisk.RiskDetections.Should().BeNull("sem nenhum registro lido não há fato — e nunca zero");
        result.IdentityRisk.RiskyUsersOutcome.Should().Be(KnightCapabilityOutcome.Collected,
            "o timeout de uma dimensão não contamina a outra");
        result.IdentityRisk.RiskyUsers!.Total.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, KnightCapabilityOutcome.AuthenticationFailure)]
    [InlineData(HttpStatusCode.Forbidden, KnightCapabilityOutcome.InsufficientPermission)]
    [InlineData(HttpStatusCode.NotFound, KnightCapabilityOutcome.LimitedByLicense)]
    [InlineData(HttpStatusCode.PaymentRequired, KnightCapabilityOutcome.LimitedByLicense)]
    [InlineData(HttpStatusCode.TooManyRequests, KnightCapabilityOutcome.Throttled)]
    [InlineData(HttpStatusCode.InternalServerError, KnightCapabilityOutcome.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, KnightCapabilityOutcome.Unavailable)]
    public async Task Transport_HttpStatus_MapsToTypedOutcome(HttpStatusCode status, KnightCapabilityOutcome expected)
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("riskyUsers", StringComparison.Ordinal)
                ? new HttpResponseMessage(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }
                : Route(req));

        var result = await CollectAsync(handler);

        result.IdentityRisk!.RiskyUsersOutcome.Should().Be(expected);
        result.IdentityRisk.RiskyUsers.Should().BeNull("uma falha nunca vira coleção vazia");
        result.IdentityRisk.RiskDetectionsOutcome.Should().Be(KnightCapabilityOutcome.Collected);
    }

    [Fact]
    public async Task Transport_ThrottledResponseWithRetryAfter_IsClassifiedAndSanitized()
    {
        var handler = new RecordingHandler(req =>
        {
            if (!req.RequestUri!.AbsoluteUri.Contains("riskDetections", StringComparison.Ordinal)) return Route(req);
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"error":{"code":"activityLimitReached","message":"slow down"}}""",
                    Encoding.UTF8, "application/json"),
            };
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return resp;
        });

        var result = await CollectAsync(handler);

        result.IdentityRisk!.RiskDetectionsOutcome.Should().Be(KnightCapabilityOutcome.Throttled);
        result.IdentityRisk.RiskDetectionsDetail.Should().Contain("HTTP 429").And.Contain("activityLimitReached");
        result.IdentityRisk.RiskDetectionsDetail.Should().NotContain("slow down", "a mensagem bruta da fonte não é repassada");
        result.IdentityRisk.RiskyUsersOutcome.Should().Be(KnightCapabilityOutcome.Collected, "429 numa dimensão não apaga a outra");
    }

    [Fact]
    public async Task Transport_LicenseErrorCode_IsLicenseNotPermission()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("riskyUsers", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("""{"error":{"code":"TenantNotLicensed"}}""", Encoding.UTF8, "application/json"),
                }
                : Route(req));

        var result = await CollectAsync(handler);

        result.IdentityRisk!.RiskyUsersOutcome.Should().Be(KnightCapabilityOutcome.LimitedByLicense,
            "403 cujo código menciona licença é limitação de PLANO, não de consentimento");
        result.IdentityRisk.RiskyUsersDetail.Should().Contain("licença");
    }

    [Fact]
    public async Task Transport_MalformedJson_IsUnavailableNotZero()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("riskDetections", StringComparison.Ordinal)
                ? Ok("{ isto não é json ]")
                : Route(req));

        var result = await CollectAsync(handler);

        result.IdentityRisk!.RiskDetectionsOutcome.Should().Be(KnightCapabilityOutcome.Unavailable);
        result.IdentityRisk.RiskDetections.Should().BeNull();
    }

    [Fact]
    public async Task Transport_MiddlePageFailure_PreservesPartialInsteadOfZero()
    {
        var handler = new RecordingHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (!url.Contains("riskDetections", StringComparison.Ordinal)) return Route(req);
            if (url.Contains("page=2", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            return Ok($$"""
                {"value":[
                  {"riskEventType":"leakedCredentials","riskLevel":"high","riskState":"atRisk","detectedDateTime":"{{Iso(Now.AddDays(-1))}}"},
                  {"riskEventType":"newCountry","riskLevel":"low","riskState":"remediated","detectedDateTime":"{{Iso(Now.AddDays(-2))}}"}],
                 "@odata.nextLink":"https://graph.microsoft.com/v1.0/identityProtection/riskDetections?page=2"}
                """);
        });

        var result = await CollectAsync(handler);

        var facts = result.IdentityRisk!.RiskDetections;
        facts.Should().NotBeNull("a falha em página intermediária PRESERVA o que já foi lido");
        facts!.TotalInWindow.Should().Be(2);
        facts.IsComplete.Should().BeFalse("os números são um piso, não o total");
        result.IdentityRisk.RiskDetectionsOutcome.Should().Be(KnightCapabilityOutcome.Unavailable);
        result.IdentityRisk.RiskDetectionsDetail.Should().Contain("leitura parcial preservada");
    }

    // ================================================================================================
    //  2) RISKY USERS — semântica
    // ================================================================================================

    [Fact]
    public async Task RiskyUsers_DistributesLevelsAndStates_SeparatingActiveFromResolved()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("riskyUsers", StringComparison.Ordinal)
                ? Ok($$"""
                    {"value":[
                      {"riskLevel":"high","riskState":"atRisk","riskLastUpdatedDateTime":"{{Iso(Now.AddDays(-1))}}"},
                      {"riskLevel":"high","riskState":"confirmedCompromised","riskLastUpdatedDateTime":"{{Iso(Now.AddDays(-3))}}"},
                      {"riskLevel":"medium","riskState":"remediated"},
                      {"riskLevel":"low","riskState":"dismissed"},
                      {"riskLevel":"none","riskState":"confirmedSafe"}]}
                    """)
                : Route(req));

        var facts = (await CollectAsync(handler)).IdentityRisk!.RiskyUsers!;

        facts.Levels.High.Should().Be(2);
        facts.Levels.Medium.Should().Be(1);
        facts.Levels.Low.Should().Be(1);
        facts.Levels.None.Should().Be(1);

        facts.States.AtRisk.Should().Be(1);
        facts.States.ConfirmedCompromised.Should().Be(1);
        facts.Active.Should().Be(2, "em aberto = atRisk + confirmedCompromised");
        facts.States.Resolved.Should().Be(3, "remediated + dismissed + confirmedSafe");
        facts.HighRiskActive.Should().Be(2, "os dois de nível alto continuam em aberto");
        facts.MostRecentRiskUpdateAt.Should().Be(Now.AddDays(-1), "freshness = a atualização mais recente");
    }

    [Fact]
    public async Task RiskyUsers_DeletedUser_IsCountedApartAndExcludedFromActiveKpis()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("riskyUsers", StringComparison.Ordinal)
                ? Ok("""
                    {"value":[
                      {"riskLevel":"high","riskState":"atRisk","isDeleted":false},
                      {"riskLevel":"high","riskState":"atRisk","isDeleted":true},
                      {"riskLevel":"high","riskState":"confirmedCompromised","isDeleted":true}]}
                    """)
                : Route(req));

        var facts = (await CollectAsync(handler)).IdentityRisk!.RiskyUsers!;

        facts.Total.Should().Be(3, "o total inclui as entradas de contas excluídas");
        facts.Deleted.Should().Be(2);
        facts.Live.Should().Be(1, "só as contas existentes entram nas distribuições");
        facts.Active.Should().Be(1, "uma conta já removida não 'exige investigação'");
        facts.HighRiskActive.Should().Be(1);
    }

    [Fact]
    public async Task RiskyUsers_UnknownFutureValue_GoesToUnknownBucket_NeverSafe()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("riskyUsers", StringComparison.Ordinal)
                ? Ok("""
                    {"value":[
                      {"riskLevel":"unknownFutureValue","riskState":"unknownFutureValue"},
                      {"riskLevel":"somethingBrandNew","riskState":"aBrandNewState"},
                      {"riskLevel":"hidden","riskState":"atRisk"}]}
                    """)
                : Route(req));

        var facts = (await CollectAsync(handler)).IdentityRisk!.RiskyUsers!;

        facts.Levels.Unknown.Should().Be(2, "valores futuros/desconhecidos preservados como desconhecidos");
        facts.Levels.Hidden.Should().Be(1, "hidden é bucket próprio — não é 'sem risco'");
        facts.Levels.None.Should().Be(0, "nada foi rebaixado para 'sem nível'");
        facts.States.Unknown.Should().Be(2);
        facts.States.Resolved.Should().Be(0, "estado desconhecido NUNCA conta como resolvido");
        facts.States.ConfirmedSafe.Should().Be(0);
        facts.Active.Should().Be(1);
    }

    [Fact]
    public async Task RiskyUsers_NullOrMissingFields_DoNotCrashAndStayUnknown()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("riskyUsers", StringComparison.Ordinal)
                ? Ok("""
                    {"value":[
                      {},
                      {"riskLevel":null,"riskState":null,"riskLastUpdatedDateTime":null},
                      {"riskLevel":"high","riskState":"atRisk","riskLastUpdatedDateTime":"not-a-date"}]}
                    """)
                : Route(req));

        var facts = (await CollectAsync(handler)).IdentityRisk!.RiskyUsers!;

        facts.Total.Should().Be(3);
        facts.Levels.Unknown.Should().Be(2);
        facts.States.Unknown.Should().Be(2);
        facts.MostRecentRiskUpdateAt.Should().BeNull("data inválida não vira freshness inventado");
        facts.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task RiskyUsers_RealZero_OnlyAfterCompleteCollection()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("riskyUsers", StringComparison.Ordinal)
                ? Ok("""{"value":[]}""")
                : Route(req));

        var risk = (await CollectAsync(handler)).IdentityRisk!;

        risk.RiskyUsersOutcome.Should().Be(KnightCapabilityOutcome.Collected);
        risk.RiskyUsers.Should().NotBeNull();
        risk.RiskyUsers!.Total.Should().Be(0, "zero é um FATO quando a coleta terminou íntegra");
        risk.RiskyUsers.IsComplete.Should().BeTrue();
    }

    // ================================================================================================
    //  3) RISK DETECTIONS — janela e semântica
    // ================================================================================================

    [Fact]
    public async Task Detections_DeterministicWindow_ExcludesOlderAndCountsRecent()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("riskDetections", StringComparison.Ordinal)
                ? Ok($$"""
                    {"value":[
                      {"riskEventType":"leakedCredentials","riskLevel":"high","riskState":"atRisk","detectionTimingType":"offline","detectedDateTime":"{{Iso(Now.AddDays(-2))}}"},
                      {"riskEventType":"newCountry","riskLevel":"medium","riskState":"remediated","detectionTimingType":"realtime","detectedDateTime":"{{Iso(Now.AddDays(-20))}}"},
                      {"riskEventType":"passwordSpray","riskLevel":"high","riskState":"atRisk","detectedDateTime":"{{Iso(Now.AddDays(-45))}}"},
                      {"riskEventType":"anomalousToken","riskLevel":"low","riskState":"dismissed"}]}
                    """)
                : Route(req));

        var facts = (await CollectAsync(handler)).IdentityRisk!.RiskDetections!;

        facts.WindowDays.Should().Be(IdentityRiskWindows.DetectionWindowDays);
        facts.WindowStart.Should().Be(Now.AddDays(-30));
        facts.WindowEnd.Should().Be(Now);
        facts.TotalInWindow.Should().Be(2, "só as de 2 e 20 dias caem na janela de 30");
        facts.OutsideWindow.Should().Be(1, "a de 45 dias é reportada, não descartada em silêncio");
        facts.Undated.Should().Be(1, "sem carimbo de tempo não dá para situar — contada à parte");
        facts.InRecentWindow.Should().Be(1, "só a de 2 dias cai na sub-janela de 7");
        facts.MostRecentDetectionAt.Should().Be(Now.AddDays(-2));
    }

    [Fact]
    public async Task Detections_SeparatesActiveFromResolved_AndTimingAndTypes()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("riskDetections", StringComparison.Ordinal)
                ? Ok($$"""
                    {"value":[
                      {"riskEventType":"leakedCredentials","riskLevel":"high","riskState":"atRisk","detectionTimingType":"realtime","detectedDateTime":"{{Iso(Now.AddDays(-1))}}"},
                      {"riskEventType":"leakedCredentials","riskLevel":"high","riskState":"confirmedCompromised","detectionTimingType":"nearRealtime","detectedDateTime":"{{Iso(Now.AddDays(-1))}}"},
                      {"riskEventType":"newCountry","riskLevel":"medium","riskState":"remediated","detectionTimingType":"offline","detectedDateTime":"{{Iso(Now.AddDays(-2))}}"},
                      {"riskEventType":"newCountry","riskLevel":"low","riskState":"unknownFutureValue","detectionTimingType":"notDefined","detectedDateTime":"{{Iso(Now.AddDays(-3))}}"},
                      {"riskEventType":"anomalousToken","riskLevel":"low","riskState":"dismissed","detectionTimingType":"brandNewTiming","detectedDateTime":"{{Iso(Now.AddDays(-4))}}"}]}
                    """)
                : Route(req));

        var facts = (await CollectAsync(handler)).IdentityRisk!.RiskDetections!;

        facts.Active.Should().Be(2);
        facts.Resolved.Should().Be(2);
        facts.States.Unknown.Should().Be(1, "estado futuro fica no bucket desconhecido");
        facts.HighRiskActive.Should().Be(2);

        facts.Realtime.Should().Be(1);
        facts.NearRealtime.Should().Be(1);
        facts.Offline.Should().Be(1);
        facts.TimingNotDefined.Should().Be(1);
        facts.TimingUnknown.Should().Be(1, "um valor de timing novo não é silenciosamente classificado");

        facts.TopTypes.Should().HaveCount(3);
        facts.TopTypes[0].Category.Should().Be("leakedcredentials");
        facts.TopTypes[0].Count.Should().Be(2);
        facts.TopTypes.Sum(t => t.Count).Should().Be(facts.TotalInWindow, "nenhum evento se perde na agregação por tipo");
    }

    [Fact]
    public async Task Detections_GenericType_IsReportedAsPremiumDetailWithheld()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("riskDetections", StringComparison.Ordinal)
                ? Ok($$"""
                    {"value":[
                      {"riskEventType":"generic","riskLevel":"hidden","riskState":"atRisk","detectedDateTime":"{{Iso(Now.AddDays(-1))}}"},
                      {"riskEventType":"generic","riskLevel":"hidden","riskState":"atRisk","detectedDateTime":"{{Iso(Now.AddDays(-2))}}"},
                      {"riskEventType":"newCountry","riskLevel":"low","riskState":"remediated","detectedDateTime":"{{Iso(Now.AddDays(-3))}}"}]}
                    """)
                : Route(req));

        var facts = (await CollectAsync(handler)).IdentityRisk!.RiskDetections!;

        // Com Entra ID P1, uma detecção PREMIUM chega como "generic" e com nível oculto: o evento é real, o
        // que falta é a classificação. A cobertura é parcial — e o fato registra isso em vez de mascarar.
        facts.PremiumDetailWithheld.Should().Be(2);
        facts.Levels.Hidden.Should().Be(2);
        facts.Levels.None.Should().Be(0, "nível oculto NUNCA vira 'sem risco'");
        facts.Active.Should().Be(2);
    }

    [Fact]
    public async Task Detections_RealZero_OnlyAfterCompleteCollection()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("riskDetections", StringComparison.Ordinal)
                ? Ok("""{"value":[]}""")
                : Route(req));

        var risk = (await CollectAsync(handler)).IdentityRisk!;

        risk.RiskDetectionsOutcome.Should().Be(KnightCapabilityOutcome.Collected);
        risk.RiskDetections!.TotalInWindow.Should().Be(0);
        risk.RiskDetections.IsComplete.Should().BeTrue();
        risk.RiskDetections.MostRecentDetectionAt.Should().BeNull();
    }

    [Fact]
    public async Task Detections_TypeCeiling_AggregatesOverflowWithoutLosingEvents()
    {
        var items = Enumerable.Range(0, 12)
            .Select(i => $$"""{"riskEventType":"type{{i}}","riskLevel":"low","riskState":"atRisk","detectedDateTime":"{{Iso(Now.AddDays(-1))}}"}""");
        var payload = $$"""{"value":[{{string.Join(",", items)}}]}""";

        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("riskDetections", StringComparison.Ordinal) ? Ok(payload) : Route(req));

        var facts = (await CollectAsync(handler)).IdentityRisk!.RiskDetections!;

        facts.TotalInWindow.Should().Be(12);
        facts.TopTypes.Should().HaveCount(IdentityRiskWindows.TopDetectionTypes + 1, "o excedente vira uma fatia 'outros'");
        facts.TopTypes.Last().Category.Should().Be(IdentityRiskVocabulary.OtherTypes);
        facts.TopTypes.Sum(t => t.Count).Should().Be(12, "nenhum evento se perde no teto de tipos");
    }

    // ================================================================================================
    //  4) INDEPENDÊNCIA DAS CAPACIDADES
    // ================================================================================================

    [Fact]
    public async Task Capabilities_AreIndependent_RiskyUsersOkDetectionsForbidden()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("riskDetections", StringComparison.Ordinal)
                ? Forbidden()
                : Route(req));

        var result = await CollectAsync(handler);

        result.IdentityRisk!.RiskyUsersOutcome.Should().Be(KnightCapabilityOutcome.Collected);
        result.IdentityRisk.RiskyUsers.Should().NotBeNull();
        result.IdentityRisk.RiskDetectionsOutcome.Should().Be(KnightCapabilityOutcome.InsufficientPermission);
        result.IdentityRisk.RiskDetections.Should().BeNull();
        result.IdentityRisk.RiskDetectionsDetail.Should().Contain(EntraIdKnightCollector.RiskDetectionsPermission);
        result.State.Should().Be(KnightSourceState.PartialCollection);
    }

    [Fact]
    public async Task Capabilities_AreIndependent_DetectionsOkRiskyUsersForbidden()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("riskyUsers", StringComparison.Ordinal)
                ? Forbidden()
                : Route(req));

        var result = await CollectAsync(handler);

        result.IdentityRisk!.RiskDetectionsOutcome.Should().Be(KnightCapabilityOutcome.Collected);
        result.IdentityRisk.RiskDetections.Should().NotBeNull();
        result.IdentityRisk.RiskyUsersOutcome.Should().Be(KnightCapabilityOutcome.InsufficientPermission);
        result.IdentityRisk.RiskyUsers.Should().BeNull();
        result.IdentityRisk.RiskyUsersDetail.Should().Contain(EntraIdKnightCollector.RiskyUsersPermission);
    }

    [Fact]
    public async Task Capabilities_BothForbidden_DoNotBreakTheRestOfTheCollection()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("identityProtection", StringComparison.Ordinal)
                ? Forbidden()
                : Route(req));

        var result = await CollectAsync(handler);

        result.IdentityRisk!.RiskyUsersOutcome.Should().Be(KnightCapabilityOutcome.InsufficientPermission);
        result.IdentityRisk.RiskDetectionsOutcome.Should().Be(KnightCapabilityOutcome.InsufficientPermission);
        result.IdentityRisk.HasAnyFacts.Should().BeFalse();

        // As capacidades preexistentes seguem intactas — o pacote novo não derruba o que já funcionava.
        result.Facts.Get(KnightSignalKey.PrivilegedAccountsTotal).IsCollected.Should().BeTrue();
        result.Facts.Get(KnightSignalKey.SecurityDefaultsEnabled).Flag.Should().BeTrue();
        result.Capabilities.Where(c => c.Capability
                is KnightCapability.IdentityRiskyUsers or KnightCapability.IdentityRiskDetections)
            .Should().HaveCount(2);
    }

    // ================================================================================================
    //  5) PRIVACIDADE
    // ================================================================================================

    [Fact]
    public async Task Privacy_NoPersonalFieldSurvivesNormalization_EvenWhenGraphReturnsThem()
    {
        // O Graph devolve TUDO (ignorando o $select): o teste prova que a normalização é a barreira real.
        var handler = new RecordingHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("riskyUsers", StringComparison.Ordinal))
                return Ok("""
                    {"value":[{"id":"PII-USER-ID","riskLevel":"high","riskState":"atRisk",
                      "userDisplayName":"PII-DISPLAY-NAME","userPrincipalName":"PII-UPN@example.com"}]}
                    """);
            if (url.Contains("riskDetections", StringComparison.Ordinal))
                return Ok($$"""
                    {"value":[{"id":"PII-DETECTION-ID","requestId":"PII-REQUEST-ID","correlationId":"PII-CORRELATION-ID",
                      "riskEventType":"leakedCredentials","riskLevel":"high","riskState":"atRisk",
                      "detectedDateTime":"{{Iso(Now.AddDays(-1))}}",
                      "ipAddress":"PII-IP-ADDRESS","location":{"city":"PII-CITY","countryOrRegion":"PII-COUNTRY"},
                      "userId":"PII-USER-ID","userDisplayName":"PII-DISPLAY-NAME","userPrincipalName":"PII-UPN@example.com",
                      "additionalInfo":"[{\"Key\":\"userAgent\",\"Value\":\"PII-USER-AGENT\"}]"}]}
                    """);
            return Route(req);
        });

        var result = await CollectAsync(handler);
        var serialized = JsonSerializer.Serialize(result);

        foreach (var sentinel in new[]
                 {
                     "PII-USER-ID", "PII-DISPLAY-NAME", "PII-UPN", "PII-DETECTION-ID", "PII-REQUEST-ID",
                     "PII-CORRELATION-ID", "PII-IP-ADDRESS", "PII-CITY", "PII-COUNTRY", "PII-USER-AGENT",
                     "SUPER-SECRET-VALUE", "fake-access-token",
                 })
            serialized.Should().NotContain(sentinel, $"'{sentinel}' jamais atravessa a normalização");

        // …e os agregados continuam corretos: a privacidade não custou a informação operacional.
        result.IdentityRisk!.RiskyUsers!.Active.Should().Be(1);
        result.IdentityRisk.RiskDetections!.Active.Should().Be(1);
    }

    [Fact]
    public async Task Privacy_FailureDetailCarriesOnlySafeDiagnostics()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("riskyUsers", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        """{"error":{"code":"Authorization_RequestDenied","message":"PII-RAW-MESSAGE with olack@adatum.com"}}""",
                        Encoding.UTF8, "application/json"),
                }
                : Route(req));

        var detail = (await CollectAsync(handler)).IdentityRisk!.RiskyUsersDetail!;

        detail.Should().Contain("HTTP 403").And.Contain("Authorization_RequestDenied");
        detail.Should().Contain("/v1.0/identityProtection/riskyUsers", "o caminho é seguro e ajuda o diagnóstico");
        detail.Should().NotContain("PII-RAW-MESSAGE").And.NotContain("olack@adatum.com");
        detail.Should().NotContain("$select", "a query string não entra no diagnóstico");
        detail.Should().NotContain("SUPER-SECRET-VALUE").And.NotContain("fake-access-token");
    }

    // ================================================================================================
    //  6) DECISÃO SOBRE UserAuthenticationMethod.Read.All
    // ================================================================================================

    [Fact]
    public async Task AuthenticationMethods_NeverQueriesPerUserEndpoint_AndUsesTheAggregateReportOnly()
    {
        var handler = new RecordingHandler(Route);
        var result = await CollectAsync(handler);

        handler.Urls.Should().NotContain(u => u.Contains("/authentication/methods", StringComparison.OrdinalIgnoreCase),
            "iterar /users/{id}/authentication/methods seria N+1 e exporia dados pessoais sem necessidade");

        // A ampliação vive no relatório AGREGADO já autorizado por AuditLog.Read.All — UMA consulta paginada.
        handler.Urls.Count(u => u.Contains("userRegistrationDetails", StringComparison.Ordinal))
            .Should().Be(1);
        var report = handler.Urls.Single(u => u.Contains("userRegistrationDetails", StringComparison.Ordinal));
        report.Should().Contain("isPasswordlessCapable").And.Contain("methodsRegistered")
            .And.Contain("isMfaCapable").And.Contain("isMfaRegistered");

        var posture = result.AuthenticationPosture!;
        posture.TotalUsers.Should().Be(3);
        posture.MfaCapable.Should().Be(2);
        posture.MfaRegistered.Should().Be(2);
        posture.PasswordlessCapable.Should().Be(1);
        posture.MfaCapableCoveragePercent.Should().BeApproximately(66.7, 0.1);
        posture.MethodsRegistered.Should().Contain(m => m.Category == "microsoftauthenticatorpush");
    }

    [Fact]
    public async Task AuthenticationMethods_EmptyReport_YieldsNullCoverageNotZeroPercent()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("userRegistrationDetails", StringComparison.Ordinal)
                ? Ok("""{"value":[]}""")
                : Route(req));

        var posture = (await CollectAsync(handler)).AuthenticationPosture!;

        posture.TotalUsers.Should().Be(0);
        posture.MfaCapableCoveragePercent.Should().BeNull("sem denominador não existe percentual — nunca 0%");
        posture.PasswordlessCoveragePercent.Should().BeNull();
    }

    // ================================================================================================
    //  7) DETERMINISMO DO RELÓGIO
    // ================================================================================================

    [Fact]
    public async Task Clock_IsInjected_SoTheWindowIsReproducible()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("riskDetections", StringComparison.Ordinal)
                ? Ok($$"""
                    {"value":[{"riskEventType":"newCountry","riskLevel":"low","riskState":"atRisk","detectedDateTime":"{{Iso(Now.AddDays(-29))}}"}]}
                    """)
                : Route(req));

        // Com o relógio no instante fixo, a detecção de 29 dias está DENTRO da janela de 30…
        var inside = await CollectAsync(handler);
        inside.IdentityRisk!.RiskDetections!.TotalInWindow.Should().Be(1);
        inside.IdentityRisk.RiskDetections.OutsideWindow.Should().Be(0);

        // …e avançando o relógio dois dias, a MESMA resposta cai para fora. A janela é do relógio, não do acaso.
        var later = new EntraIdKnightCollector(
            new EntraGraphClient(new HttpClient(new RecordingHandler(req =>
                req.RequestUri!.AbsoluteUri.Contains("riskDetections", StringComparison.Ordinal)
                    ? Ok($$"""
                        {"value":[{"riskEventType":"newCountry","riskLevel":"low","riskState":"atRisk","detectedDateTime":"{{Iso(Now.AddDays(-29))}}"}]}
                        """)
                    : Route(req)))),
            null, new FixedTime(Now.AddDays(2)));
        var outside = await later.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        outside.IdentityRisk!.RiskDetections!.TotalInWindow.Should().Be(0);
        outside.IdentityRisk.RiskDetections.OutsideWindow.Should().Be(1);
        outside.CollectedAt.Should().Be(Now.AddDays(2), "o carimbo da coleta também vem do relógio injetado");
    }

    // ================================================================================================
    //  Infraestrutura do teste
    // ================================================================================================

    private static EntraIdKnightCollector Collector(HttpMessageHandler handler) =>
        new(new EntraGraphClient(new HttpClient(handler)), null, new FixedTime(Now));

    private static async Task<KnightCollectionResult> CollectAsync(HttpMessageHandler handler) =>
        await Collector(handler).CollectAsync(new KnightCollectionContext(Tenant, Cfg));

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Forbidden() =>
        new(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":{"code":"Authorization_RequestDenied"}}""", Encoding.UTF8, "application/json"),
        };

    /// <summary>Respostas mínimas e VÁLIDAS para todas as capacidades — o cenário base dos testes.</summary>
    private static HttpResponseMessage Route(HttpRequestMessage req)
    {
        var url = req.RequestUri!.AbsoluteUri;

        if (req.Method == HttpMethod.Post && url.Contains("/oauth2/v2.0/token", StringComparison.Ordinal))
            return Ok(TokenJson);

        if (url.Contains("identityProtection/riskyUsers", StringComparison.Ordinal))
            return Ok($$"""
                {"value":[
                  {"riskLevel":"high","riskState":"atRisk","riskLastUpdatedDateTime":"{{Iso(Now.AddDays(-1))}}"},
                  {"riskLevel":"low","riskState":"remediated","riskLastUpdatedDateTime":"{{Iso(Now.AddDays(-5))}}"}]}
                """);

        if (url.Contains("identityProtection/riskDetections", StringComparison.Ordinal))
            return Ok($$"""
                {"value":[
                  {"riskEventType":"unfamiliarFeatures","riskLevel":"medium","riskState":"atRisk","detectionTimingType":"realtime","detectedDateTime":"{{Iso(Now.AddDays(-2))}}"}]}
                """);

        if (url.Contains("userRegistrationDetails", StringComparison.Ordinal))
            return Ok("""
                {"value":[
                  {"id":"u1","isMfaCapable":true,"isMfaRegistered":true,"isPasswordlessCapable":true,"methodsRegistered":["microsoftAuthenticatorPush","passKeyDeviceBound"]},
                  {"id":"u2","isMfaCapable":false,"isMfaRegistered":false,"isPasswordlessCapable":false,"methodsRegistered":[]},
                  {"id":"u3","isMfaCapable":true,"isMfaRegistered":true,"isPasswordlessCapable":false,"methodsRegistered":["mobilePhone"]}]}
                """);

        if (url.Contains("/directoryRoles/", StringComparison.Ordinal) && url.Contains("/members", StringComparison.Ordinal))
            return Ok("""{"value":[{"id":"u1","userType":"Member","signInActivity":{"lastSignInDateTime":"__RECENT__"}}]}"""
                .Replace("__RECENT__", Iso(Now.AddDays(-1))));

        if (url.Contains("/directoryRoles", StringComparison.Ordinal))
            return Ok("""{"value":[{"id":"role1","displayName":"Company Administrator"}]}""");

        if (url.Contains("identitySecurityDefaultsEnforcementPolicy", StringComparison.Ordinal))
            return Ok("""{"isEnabled":true}""");

        if (url.Contains("servicePrincipals", StringComparison.Ordinal) && !url.Contains("appRoleAssignedTo", StringComparison.Ordinal))
            return Ok("""{"id":"graph-sp"}""");

        // users (guests), conditionalAccess, applications, appRoleAssignedTo, oauth2PermissionGrants…
        return Ok("""{"value":[]}""");
    }

    /// <summary>Relógio fixo — nenhuma janela deste coletor depende do relógio da máquina de teste.</summary>
    private sealed class FixedTime : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTime(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// Handler que REGISTRA cada requisição (URL, host e se levou Authorization) além de responder. É o que
    /// permite afirmar, e não supor, quantos tokens foram adquiridos e para onde o bearer foi enviado.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _map;

        public List<string> Urls { get; } = new();
        public List<string> AuthorizedHosts { get; } = new();
        public int TokenRequests { get; private set; }

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> map) => _map = map;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = request.RequestUri!.AbsoluteUri;
            Urls.Add(url);
            if (request.Headers.Authorization is not null) AuthorizedHosts.Add(request.RequestUri.Host);
            if (request.Method == HttpMethod.Post && url.Contains("/oauth2/v2.0/token", StringComparison.Ordinal)) TokenRequests++;

            return Task.FromResult(_map(request));
        }
    }
}
