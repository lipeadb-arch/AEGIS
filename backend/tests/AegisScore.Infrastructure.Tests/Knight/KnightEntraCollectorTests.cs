using System;
using System.Collections.Generic;
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
/// Testes do coletor REAL do Microsoft Entra ID por HTTP SIMULADO (sem rede, sem credenciais reais): exercitam
/// o protocolo verdadeiro nas URLs OFICIAIS (login.microsoftonline.com / graph.microsoft.com) — client
/// credentials, Bearer, paginação por <c>@odata.nextLink</c>, normalização das respostas em fatos, tratamento
/// de 403 (permissão insuficiente → parcial → NotEvaluated) — e as CORREÇÕES desta entrega: rejeição de
/// nextLink de origem arbitrária (o bearer não vaza), teto de paginação que NÃO trunca em silêncio, MFA vazio/
/// malformado como NotEvaluated (nunca 100%), mailbox não comprovada na fonte real, permissões CONCEDIDAS
/// (appRoleAssignments) × solicitadas, consentimento DELEGADO tenant-wide, estados reais de falha preservados,
/// e o segredo fora do <c>ToString</c>.
/// </summary>
public sealed class KnightEntraCollectorTests
{
    // Sem base URLs no config: o destino é constante oficial no cliente HTTP (o tenant nunca fornece URL).
    private static readonly KnightEntraIdConfiguration Cfg = new(
        AzureTenantId: "11111111-2222-3333-4444-555555555555",
        ClientId: "app-client-id",
        ClientSecret: "SUPER-SECRET-VALUE");

    private static readonly Guid Tenant = Guid.Parse("aa000000-0000-0000-0000-000000000001");

    private static readonly string Recent = DateTimeOffset.UtcNow.AddDays(-1).ToString("o");
    private static readonly string Old = DateTimeOffset.UtcNow.AddDays(-120).ToString("o");

    private const string TokenJson = """{"access_token":"fake-access-token","expires_in":3600,"token_type":"Bearer"}""";

    /// <summary>
    /// Usuários em risco do cenário feliz: alto/atRisk, médio/confirmedCompromised, baixo/remediated e UM
    /// EXCLUÍDO (que fica fora das distribuições, contado à parte). Nenhum campo pessoal — o $select do
    /// coletor sequer os solicita.
    /// </summary>
    private static readonly string RiskyUsersJson = """
        {"value":[
          {"riskLevel":"high","riskState":"atRisk","isDeleted":false,"isProcessing":false,"riskLastUpdatedDateTime":"__RECENT__"},
          {"riskLevel":"medium","riskState":"confirmedCompromised","isDeleted":false,"isProcessing":true,"riskLastUpdatedDateTime":"__RECENT__"},
          {"riskLevel":"low","riskState":"remediated","isDeleted":false,"isProcessing":false,"riskLastUpdatedDateTime":"__RECENT__"},
          {"riskLevel":"high","riskState":"atRisk","isDeleted":true,"riskLastUpdatedDateTime":"__RECENT__"}
        ]}
        """.Replace("__RECENT__", Recent);

    /// <summary>
    /// Detecções do cenário feliz: duas dentro da janela (uma delas <c>generic</c> = detalhe premium retido) e
    /// uma FORA da janela de 30 dias, que não pode contaminar os agregados recentes.
    /// </summary>
    private static readonly string RiskDetectionsJson = """
        {"value":[
          {"riskEventType":"unfamiliarFeatures","riskState":"atRisk","riskLevel":"high","detectionTimingType":"realtime","detectedDateTime":"__RECENT__"},
          {"riskEventType":"generic","riskState":"remediated","riskLevel":"hidden","detectionTimingType":"offline","detectedDateTime":"__RECENT__"},
          {"riskEventType":"leakedCredentials","riskState":"atRisk","riskLevel":"medium","detectionTimingType":"offline","detectedDateTime":"__OLD__"}
        ]}
        """.Replace("__RECENT__", Recent).Replace("__OLD__", Old);

    // ---- 1) Normaliza respostas do Graph em fatos (coleta completa) --------------------------------

    [Fact]
    public async Task Collector_NormalizesGraphResponses_Completed()
    {
        var collector = new EntraIdKnightCollector(new EntraGraphClient(new HttpClient(HappyHandler())));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        result.State.Should().Be(KnightSourceState.Completed);
        result.Source.Should().Be(KnightSourceType.MicrosoftEntraId);

        result.Facts.Get(KnightSignalKey.PrivilegedAccountsTotal).Count.Should().Be(2);          // u1, u2
        result.Facts.Get(KnightSignalKey.ExternalMembersInPrivilegedRoles).Count.Should().Be(1); // u2 Guest
        result.Facts.Get(KnightSignalKey.PrivilegedAccountsWithoutMfa).Count.Should().Be(1);      // u2 sem MFA capaz
        result.Facts.Get(KnightSignalKey.MfaRegistrationCoveragePercent).Ratio.Should().BeApproximately(66.7, 0.2); // 2 de 3
        result.Facts.Get(KnightSignalKey.InactiveGuestAccounts).Count.Should().Be(1);             // g1 inativo (120d)
        result.Facts.Get(KnightSignalKey.SecurityDefaultsEnabled).Flag.Should().BeTrue();

        // [AEGIS-MVP-MICROSOFT-COVERAGE-03] As duas capacidades novas entram na MESMA coleta lógica.
        var risk = result.IdentityRisk.Should().NotBeNull().And.Subject.As<IdentityRiskPosture>();
        risk.RiskyUsersOutcome.Should().Be(KnightCapabilityOutcome.Collected);
        risk.RiskDetectionsOutcome.Should().Be(KnightCapabilityOutcome.Collected);

        risk.RiskyUsers!.Total.Should().Be(4);              // inclui o excluído
        risk.RiskyUsers.Deleted.Should().Be(1);
        risk.RiskyUsers.Live.Should().Be(3);                // o excluído fica FORA das distribuições
        risk.RiskyUsers.Active.Should().Be(2);              // atRisk + confirmedCompromised
        risk.RiskyUsers.HighRiskActive.Should().Be(1);
        risk.RiskyUsers.States.Resolved.Should().Be(1);     // remediated
        risk.RiskyUsers.Processing.Should().Be(1);
        risk.RiskyUsers.IsComplete.Should().BeTrue();

        risk.RiskDetections!.TotalInWindow.Should().Be(2);  // a de 120 dias fica de fora
        risk.RiskDetections.OutsideWindow.Should().Be(1);
        risk.RiskDetections.Active.Should().Be(1);
        risk.RiskDetections.PremiumDetailWithheld.Should().Be(1);   // "generic" = detalhe retido por licença
        risk.RiskDetections.Levels.Hidden.Should().Be(1);           // nível oculto ≠ "sem risco"
        risk.RiskDetections.WindowDays.Should().Be(IdentityRiskWindows.DetectionWindowDays);

        // Postura AGREGADA de métodos: derivada do MESMO relatório já autorizado (sem permissão nova).
        result.AuthenticationPosture!.TotalUsers.Should().Be(3);
        result.AuthenticationPosture.MfaCapable.Should().Be(2);
    }

    // ---- 2) Paginação por @odata.nextLink (URL oficial) --------------------------------------------

    [Fact]
    public async Task GraphClient_FollowsPagination()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("p=2")) return (HttpStatusCode.OK, """{"value":[{"id":"2"}]}""");
            if (url.EndsWith("/v1.0/foo")) return (HttpStatusCode.OK, """{"value":[{"id":"1"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/foo?p=2"}""");
            return (HttpStatusCode.NotFound, "{}");
        });
        var client = new EntraGraphClient(new HttpClient(handler));

        var ids = new List<string>();
        await foreach (var item in client.GetPagedAsync("tok", Cfg, "foo", CancellationToken.None))
            ids.Add(item.GetProperty("id").GetString()!);

        ids.Should().Equal("1", "2");
    }

    // ---- 3) 403 numa capacidade → permissão insuficiente → parcial → indicadores NotEvaluated ------

    [Fact]
    public async Task Collector_InsufficientPermission_YieldsPartialAndNotEvaluated()
    {
        // Tudo OK, EXCETO applications → 403 (Application.Read.All ausente).
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("/applications")) return (HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied"}}""");
            return Happy(req);
        });
        var collector = new EntraIdKnightCollector(new EntraGraphClient(new HttpClient(handler)));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        result.State.Should().Be(KnightSourceState.PartialCollection);
        result.Capabilities.Should().Contain(c =>
            c.Capability == KnightCapability.ApplicationInventory && c.Outcome == KnightCapabilityOutcome.InsufficientPermission);
        result.Facts.Get(KnightSignalKey.ApplicationCredentialsExpiring).Outcome.Should().Be(KnightObservationOutcome.Missing);

        // O indicador dependente vira NotEvaluated (NUNCA Passed).
        var appCreds = KnightIndicatorEvaluator.Evaluate(result.Facts, KnightSourceType.MicrosoftEntraId)
            .Single(i => i.Definition.Id == "AK-ENTRA-009");
        appCreds.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        appCreds.NotEvaluatedReason.Should().NotBeNullOrWhiteSpace();
    }

    // ---- 4) Token/segredo não vazam no resultado da coleta ----------------------------------------

    [Fact]
    public async Task Collector_TokenAndSecret_NotExposedInResult()
    {
        var collector = new EntraIdKnightCollector(new EntraGraphClient(new HttpClient(HappyHandler())));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));
        var serialized = JsonSerializer.Serialize(result);

        serialized.Should().NotContain("SUPER-SECRET-VALUE", "o segredo do cliente nunca aparece no resultado");
        serialized.Should().NotContain("fake-access-token", "o token de acesso nunca aparece no resultado");
    }

    // ---- 5) nextLink malicioso: nenhuma requisição chega ao host malicioso; bearer não é enviado ---

    [Fact]
    public async Task Collector_MaliciousNextLink_CapabilityUnavailable_NoBearerToEvilHost()
    {
        var evilHits = 0;
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("evil.example")) { evilHits++; return (HttpStatusCode.OK, """{"value":[]}"""); }
            // 1ª página de guests é VÁLIDA, mas devolve um @odata.nextLink de origem arbitrária.
            if (url.Contains("/users") && url.Contains("Guest"))
                return (HttpStatusCode.OK, """{"value":[{"id":"g1"}],"@odata.nextLink":"https://evil.example/v1.0/users?p=2"}""");
            return Happy(req);
        });
        var collector = new EntraIdKnightCollector(new EntraGraphClient(new HttpClient(handler)));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        evilHits.Should().Be(0, "o nextLink de outra origem é reprovado ANTES do envio — o bearer nunca sai para ele");
        result.Capabilities.Should().Contain(c =>
            c.Capability == KnightCapability.GuestAccounts && c.Outcome == KnightCapabilityOutcome.Unavailable);
        result.Facts.Get(KnightSignalKey.InactiveGuestAccounts).Outcome.Should().Be(KnightObservationOutcome.Missing);

        var guest = KnightIndicatorEvaluator.Evaluate(result.Facts, KnightSourceType.MicrosoftEntraId)
            .Single(i => i.Definition.Id == "AK-ENTRA-004");
        guest.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
    }

    // ---- 6) Teto de paginação NÃO trunca em silêncio: lança e a capacidade fica indisponível --------

    [Fact]
    public async Task GraphClient_PaginationCapExceeded_Throws()
    {
        // Sempre devolve nextLink (nunca termina). Com teto=1, a página seguinte é recusada.
        var handler = new StubHandler(_ =>
            (HttpStatusCode.OK, """{"value":[{"id":"1"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/foo?p=next"}"""));
        var client = new EntraGraphClient(new HttpClient(handler), maxPages: 1);

        var act = async () =>
        {
            await foreach (var _ in client.GetPagedAsync("tok", Cfg, "foo", CancellationToken.None)) { }
        };

        await act.Should().ThrowAsync<EntraGraphException>();
    }

    [Fact]
    public async Task Collector_PaginationCap_CapabilityUnavailable_IndicatorNotEvaluated()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (req.Method == HttpMethod.Post && url.Contains("/oauth2/v2.0/token")) return (HttpStatusCode.OK, TokenJson);
            // directoryRoles pagina infinitamente → com teto 1 a capacidade de privilegiadas fica indisponível.
            if (url.Contains("directoryRoles"))
                return (HttpStatusCode.OK, """{"value":[{"id":"role1"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/directoryRoles?p=next"}""");
            return Happy(req);
        });
        var collector = new EntraIdKnightCollector(new EntraGraphClient(new HttpClient(handler), maxPages: 1));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        result.Capabilities.Should().Contain(c =>
            c.Capability == KnightCapability.PrivilegedRoleInventory && c.Outcome == KnightCapabilityOutcome.Unavailable);
        var total = KnightIndicatorEvaluator.Evaluate(result.Facts, KnightSourceType.MicrosoftEntraId)
            .Single(i => i.Definition.Id == "AK-ENTRA-002");
        total.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
    }

    // ---- 7) MFA vazio/malformado → NotEvaluated (fail-closed, nunca 100%) ---------------------------

    [Fact]
    public async Task Collector_MfaEmptyReport_NotEvaluated_NeverHundredPercent()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("userRegistrationDetails")) return (HttpStatusCode.OK, """{"value":[]}""");
            return Happy(req);
        });
        var collector = new EntraIdKnightCollector(new EntraGraphClient(new HttpClient(handler)));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        result.Facts.Get(KnightSignalKey.MfaRegistrationCoveragePercent).Outcome.Should().Be(KnightObservationOutcome.Missing);
        var mfa = KnightIndicatorEvaluator.Evaluate(result.Facts, KnightSourceType.MicrosoftEntraId)
            .Single(i => i.Definition.Id == "AK-ENTRA-006");
        mfa.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
    }

    [Fact]
    public async Task Collector_MfaMalformed_NotEvaluated()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            // isMfaCapable como string ("yes") é malformado — não vira "false" em silêncio.
            if (url.Contains("userRegistrationDetails")) return (HttpStatusCode.OK, """{"value":[{"id":"u1","isMfaCapable":"yes"}]}""");
            return Happy(req);
        });
        var collector = new EntraIdKnightCollector(new EntraGraphClient(new HttpClient(handler)));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        result.Facts.Get(KnightSignalKey.MfaRegistrationCoveragePercent).Outcome.Should().Be(KnightObservationOutcome.Missing);
        KnightIndicatorEvaluator.Evaluate(result.Facts, KnightSourceType.MicrosoftEntraId)
            .Single(i => i.Definition.Id == "AK-ENTRA-006").Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
    }

    // ---- 8) Mailbox não é comprovada por diretório na fonte real → AK-ENTRA-003 NotEvaluated --------

    [Fact]
    public async Task Collector_Mailbox_NotEvaluatedOnRealSource()
    {
        var collector = new EntraIdKnightCollector(new EntraGraphClient(new HttpClient(HappyHandler())));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        result.Facts.Get(KnightSignalKey.PrivilegedAccountsWithMailbox).Outcome.Should().Be(KnightObservationOutcome.Missing);
        KnightIndicatorEvaluator.Evaluate(result.Facts, KnightSourceType.MicrosoftEntraId)
            .Single(i => i.Definition.Id == "AK-ENTRA-003").Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
    }

    // ---- 9) AK-ENTRA-010: conta permissões CONCEDIDAS (appRoleAssignments), não solicitadas --------

    [Fact]
    public async Task Collector_HighPrivilegeApps_CountsGrantedAssignments_NotRequiredResourceAccess()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            // Concessão real de Directory.ReadWrite.All a UM service principal; a atribuição a um usuário é ignorada.
            if (url.Contains("appRoleAssignedTo"))
                return (HttpStatusCode.OK, """{"value":[{"principalId":"sp-1","principalType":"ServicePrincipal","appRoleId":"19dbc75e-c2e2-444c-a770-ec69d8559fc7"},{"principalId":"user-1","principalType":"User","appRoleId":"19dbc75e-c2e2-444c-a770-ec69d8559fc7"}]}""");
            if (url.Contains("servicePrincipals")) return (HttpStatusCode.OK, """{"id":"graph-sp"}""");
            // A aplicação DECLARA (requiredResourceAccess) alto privilégio, mas declaração ≠ concessão → não conta.
            if (url.Contains("/applications"))
                return (HttpStatusCode.OK, """{"value":[{"id":"app-1","requiredResourceAccess":[{"resourceAppId":"00000003-0000-0000-c000-000000000000","resourceAccess":[{"id":"19dbc75e-c2e2-444c-a770-ec69d8559fc7","type":"Role"}]}]}]}""");
            return Happy(req);
        });
        var collector = new EntraIdKnightCollector(new EntraGraphClient(new HttpClient(handler)));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        result.Facts.Get(KnightSignalKey.HighPrivilegeApplications).Count.Should().Be(1, "só a atribuição CONCEDIDA a um service principal conta");
        KnightIndicatorEvaluator.Evaluate(result.Facts, KnightSourceType.MicrosoftEntraId)
            .Single(i => i.Definition.Id == "AK-ENTRA-010").Status.Should().Be(KnightIndicatorStatus.Exposed);
    }

    // ---- 10) AK-ENTRA-013: consentimento DELEGADO tenant-wide (AllPrincipals), Principal não conta --

    [Fact]
    public async Task Collector_DelegatedConsents_CountsAllPrincipalsOnly_NotSingleUser()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("oauth2PermissionGrants"))
                return (HttpStatusCode.OK, """{"value":[{"clientId":"c-1","consentType":"AllPrincipals"},{"clientId":"c-1","consentType":"AllPrincipals"},{"clientId":"c-2","consentType":"Principal","principalId":"u-9"}]}""");
            return Happy(req);
        });
        var collector = new EntraIdKnightCollector(new EntraGraphClient(new HttpClient(handler)));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        result.Facts.Get(KnightSignalKey.AdminConsentedApplications).Count.Should().Be(1, "clientIds únicos com AllPrincipals; Principal (usuário único) não entra no total tenant-wide");
        KnightIndicatorEvaluator.Evaluate(result.Facts, KnightSourceType.MicrosoftEntraId)
            .Single(i => i.Definition.Id == "AK-ENTRA-013").Status.Should().Be(KnightIndicatorStatus.Exposed);
    }

    // ---- 11) Estados reais de falha preservados na agregação: tudo em throttling → Throttled --------

    [Fact]
    public async Task Collector_AllCapabilitiesThrottled_YieldsThrottledState()
    {
        // Token OK; toda consulta de DADOS devolve 429 → todas as capacidades em throttling.
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (req.Method == HttpMethod.Post && url.Contains("/oauth2/v2.0/token")) return (HttpStatusCode.OK, TokenJson);
            return (HttpStatusCode.TooManyRequests, """{"error":{"code":"TooManyRequests"}}""");
        });
        var collector = new EntraIdKnightCollector(new EntraGraphClient(new HttpClient(handler)));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        result.State.Should().Be(KnightSourceState.Throttled);
        result.Capabilities.Should().OnlyContain(c => c.Outcome == KnightCapabilityOutcome.Throttled);
    }

    // ---- 12) O ToString da configuração NÃO expõe o ClientSecret -----------------------------------

    [Fact]
    public void Configuration_ToString_DoesNotExposeSecret()
    {
        Cfg.ToString().Should().NotContain("SUPER-SECRET-VALUE", "records imprimem membros por padrão — o segredo foi redigido");
        Cfg.ToString().Should().Contain("***");
    }

    // ---- 13) Conditional Access: só cobertura GLOBAL comprovada aprova ------------------------------

    public static IEnumerable<object[]> CaCases() => new[]
    {
        // MFA administrativa: um único includeRole não prova cobertura completa.
        new object[] { """{"value":[{"state":"enabled","conditions":{"users":{"includeRoles":["role-x"]},"applications":{"includeApplications":["All"]}},"grantControls":{"builtInControls":["mfa"]}}]}""", "AdminMfa", false },
        // MFA para All users / All apps, sem exclusões → comprovada.
        new object[] { """{"value":[{"state":"enabled","conditions":{"users":{"includeUsers":["All"]},"applications":{"includeApplications":["All"]}},"grantControls":{"builtInControls":["mfa"]}}]}""", "AdminMfa", true },
        // Bloqueio legado com excludeUsers → não comprovado.
        new object[] { """{"value":[{"state":"enabled","conditions":{"clientAppTypes":["exchangeActiveSync"],"users":{"includeUsers":["All"],"excludeUsers":["u-1"]},"applications":{"includeApplications":["All"]}},"grantControls":{"builtInControls":["block"]}}]}""", "Legacy", false },
        // Bloqueio legado com excludeGroups → não comprovado.
        new object[] { """{"value":[{"state":"enabled","conditions":{"clientAppTypes":["exchangeActiveSync"],"users":{"includeUsers":["All"],"excludeGroups":["g-1"]},"applications":{"includeApplications":["All"]}},"grantControls":{"builtInControls":["block"]}}]}""", "Legacy", false },
        // Bloqueio legado para All users / All apps, sem exclusões → comprovado.
        new object[] { """{"value":[{"state":"enabled","conditions":{"clientAppTypes":["exchangeActiveSync"],"users":{"includeUsers":["All"]},"applications":{"includeApplications":["All"]}},"grantControls":{"builtInControls":["block"]}}]}""", "Legacy", true },
    };

    [Theory]
    [MemberData(nameof(CaCases))]
    public async Task ConditionalAccess_OnlyGlobalCoverageProves(string policiesJson, string which, bool expected)
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("conditionalAccess/policies")) return (HttpStatusCode.OK, policiesJson);
            return Happy(req);
        });
        var collector = new EntraIdKnightCollector(new EntraGraphClient(new HttpClient(handler)));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        var key = which == "AdminMfa" ? KnightSignalKey.AdminMfaPolicyEnforced : KnightSignalKey.LegacyAuthenticationBlocked;
        result.Facts.Get(key).Flag.Should().Be(expected);
    }

    // ---- 14) Cobertura de MFA/atividade de privilegiados deve ser COMPLETA -------------------------

    [Fact]
    public async Task Collector_PrivilegedUserAbsentFromMfaReport_NotEvaluated()
    {
        // admin-1 é privilegiado, mas NÃO aparece no relatório de registro de MFA → cobertura incompleta.
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("/directoryRoles/") && url.Contains("/members"))
                return (HttpStatusCode.OK, """{"value":[{"id":"admin-1","userType":"Member","signInActivity":{"lastSignInDateTime":"__RECENT__"}}]}""".Replace("__RECENT__", Recent));
            if (url.Contains("userRegistrationDetails"))
                return (HttpStatusCode.OK, """{"value":[{"id":"someone-else","isMfaCapable":true}]}""");
            return Happy(req);
        });
        var collector = new EntraIdKnightCollector(new EntraGraphClient(new HttpClient(handler)));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        result.Facts.Get(KnightSignalKey.PrivilegedAccountsWithoutMfa).Outcome.Should().Be(KnightObservationOutcome.Missing);
        KnightIndicatorEvaluator.Evaluate(result.Facts, KnightSourceType.MicrosoftEntraId)
            .Single(i => i.Definition.Id == "AK-ENTRA-001").Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
    }

    [Fact]
    public async Task Collector_AllPrivilegedPresentInReport_ComputesWithoutMfa()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("/directoryRoles/") && url.Contains("/members"))
                return (HttpStatusCode.OK, """{"value":[{"id":"admin-1","userType":"Member","signInActivity":{"lastSignInDateTime":"__RECENT__"}}]}""".Replace("__RECENT__", Recent));
            if (url.Contains("userRegistrationDetails"))
                return (HttpStatusCode.OK, """{"value":[{"id":"admin-1","isMfaCapable":false}]}""");
            return Happy(req);
        });
        var collector = new EntraIdKnightCollector(new EntraGraphClient(new HttpClient(handler)));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        result.Facts.Get(KnightSignalKey.PrivilegedAccountsWithoutMfa).Count.Should().Be(1);
        KnightIndicatorEvaluator.Evaluate(result.Facts, KnightSourceType.MicrosoftEntraId)
            .Single(i => i.Definition.Id == "AK-ENTRA-001").Status.Should().Be(KnightIndicatorStatus.Exposed);
    }

    [Fact]
    public async Task Collector_PartialPrivilegedActivity_StaleNotEvaluated()
    {
        // admin-1 tem atividade recente; admin-2 não tem atividade → não calcula obsolescência sobre o subconjunto.
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("/directoryRoles/") && url.Contains("/members"))
                return (HttpStatusCode.OK, """{"value":[{"id":"admin-1","userType":"Member","signInActivity":{"lastSignInDateTime":"__RECENT__"}},{"id":"admin-2","userType":"Member"}]}""".Replace("__RECENT__", Recent));
            if (url.Contains("userRegistrationDetails"))
                return (HttpStatusCode.OK, """{"value":[{"id":"admin-1","isMfaCapable":true},{"id":"admin-2","isMfaCapable":true}]}""");
            return Happy(req);
        });
        var collector = new EntraIdKnightCollector(new EntraGraphClient(new HttpClient(handler)));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        result.Facts.Get(KnightSignalKey.StalePrivilegedAccounts).Outcome.Should().Be(KnightObservationOutcome.Missing);
        KnightIndicatorEvaluator.Evaluate(result.Facts, KnightSourceType.MicrosoftEntraId)
            .Single(i => i.Definition.Id == "AK-ENTRA-011").Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
    }

    // ---- 15) Token JSON malformado vira AuthenticationFailure sanitizada (sem throw bruto) ----------

    [Fact]
    public async Task Collector_InvalidTokenJson_AuthenticationFailure_NoRawThrow()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (req.Method == HttpMethod.Post && url.Contains("/oauth2/v2.0/token")) return (HttpStatusCode.OK, "{ not valid json ");
            return Happy(req);
        });
        var collector = new EntraIdKnightCollector(new EntraGraphClient(new HttpClient(handler)));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));   // não deve lançar

        result.State.Should().Be(KnightSourceState.AuthenticationFailure);
    }

    [Fact]
    public async Task Collector_AccessTokenWrongType_AuthenticationFailure()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (req.Method == HttpMethod.Post && url.Contains("/oauth2/v2.0/token")) return (HttpStatusCode.OK, """{"access_token":12345,"token_type":"Bearer"}""");
            return Happy(req);
        });
        var collector = new EntraIdKnightCollector(new EntraGraphClient(new HttpClient(handler)));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        result.State.Should().Be(KnightSourceState.AuthenticationFailure);
    }

    [Fact]
    public async Task Collector_MalformedToken_ResultHasNoSecretOrPayload()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (req.Method == HttpMethod.Post && url.Contains("/oauth2/v2.0/token")) return (HttpStatusCode.OK, """{"access_token":{"leak":"PAYLOAD-MARKER"}}""");
            return Happy(req);
        });
        var collector = new EntraIdKnightCollector(new EntraGraphClient(new HttpClient(handler)));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));
        var serialized = JsonSerializer.Serialize(result);

        result.State.Should().Be(KnightSourceState.AuthenticationFailure);
        serialized.Should().NotContain("SUPER-SECRET-VALUE", "o segredo do cliente nunca aparece no resultado");
        serialized.Should().NotContain("PAYLOAD-MARKER", "o payload da resposta do token não vaza no resultado");
    }

    // ---- HTTP simulado -----------------------------------------------------------------------------

    private static HttpMessageHandler HappyHandler() => new StubHandler(Happy);

    /// <summary>Respostas "felizes" do Graph (token + capacidades) para o cenário de coleta completa.</summary>
    private static (HttpStatusCode, string) Happy(HttpRequestMessage req)
    {
        var url = req.RequestUri!.AbsoluteUri;

        if (req.Method == HttpMethod.Post && url.Contains("/oauth2/v2.0/token"))
            return (HttpStatusCode.OK, TokenJson);

        // [AEGIS-MVP-MICROSOFT-COVERAGE-03] Identity Protection — as duas capacidades novas respondem no
        // cenário feliz (do contrário a coleta inteira viraria PartialCollection por 404).
        if (url.Contains("identityProtection/riskyUsers"))
            return (HttpStatusCode.OK, RiskyUsersJson);

        if (url.Contains("identityProtection/riskDetections"))
            return (HttpStatusCode.OK, RiskDetectionsJson);

        if (url.Contains("/directoryRoles/") && url.Contains("/members"))
            return (HttpStatusCode.OK,
                """{"value":[{"id":"u1","userType":"Member","signInActivity":{"lastSignInDateTime":"__RECENT__"}},{"id":"u2","userType":"Guest"}]}"""
                    .Replace("__RECENT__", Recent));

        if (url.Contains("/directoryRoles"))
            return (HttpStatusCode.OK, """{"value":[{"id":"role1","displayName":"Company Administrator"}]}""");

        if (url.Contains("userRegistrationDetails"))
            return (HttpStatusCode.OK, """{"value":[{"id":"u1","isMfaCapable":true},{"id":"u2","isMfaCapable":false},{"id":"u3","isMfaCapable":true}]}""");

        if (url.Contains("/users") && url.Contains("Guest"))
            return (HttpStatusCode.OK,
                """{"value":[{"id":"g1","signInActivity":{"lastSignInDateTime":"__OLD__"}}]}""".Replace("__OLD__", Old));

        if (url.Contains("conditionalAccess/policies"))
            return (HttpStatusCode.OK, """{"value":[]}""");

        if (url.Contains("identitySecurityDefaultsEnforcementPolicy"))
            return (HttpStatusCode.OK, """{"isEnabled":true}""");

        if (url.Contains("/applications"))
            return (HttpStatusCode.OK, """{"value":[]}""");

        // Permissões CONCEDIDAS + consentimentos delegados (vazios no cenário feliz → capacidades coletadas).
        if (url.Contains("appRoleAssignedTo"))
            return (HttpStatusCode.OK, """{"value":[]}""");

        if (url.Contains("servicePrincipals"))
            return (HttpStatusCode.OK, """{"id":"graph-sp"}""");

        if (url.Contains("oauth2PermissionGrants"))
            return (HttpStatusCode.OK, """{"value":[]}""");

        return (HttpStatusCode.NotFound, "{}");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode, string)> _map;
        public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> map) => _map = map;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (code, body) = _map(request);
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
