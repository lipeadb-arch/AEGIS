using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

    // ---- HTTP simulado -----------------------------------------------------------------------------

    private static HttpMessageHandler HappyHandler() => new StubHandler(Happy);

    /// <summary>Respostas "felizes" do Graph (token + capacidades) para o cenário de coleta completa.</summary>
    private static (HttpStatusCode, string) Happy(HttpRequestMessage req)
    {
        var url = req.RequestUri!.AbsoluteUri;

        if (req.Method == HttpMethod.Post && url.Contains("/oauth2/v2.0/token"))
            return (HttpStatusCode.OK, TokenJson);

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
