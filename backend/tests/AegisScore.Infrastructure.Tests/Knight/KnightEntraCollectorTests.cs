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
/// o protocolo verdadeiro — client credentials, Bearer, paginação por <c>@odata.nextLink</c>, normalização das
/// respostas do Graph em fatos, tratamento de 403 (permissão insuficiente → coleta parcial → NotEvaluated) e
/// a garantia de que token/segredo não vazam no resultado.
/// </summary>
public sealed class KnightEntraCollectorTests
{
    private static readonly KnightEntraIdConfiguration Cfg = new(
        AzureTenantId: "11111111-2222-3333-4444-555555555555",
        ClientId: "app-client-id",
        ClientSecret: "SUPER-SECRET-VALUE",
        GraphBaseUrl: "https://graph.test",
        LoginBaseUrl: "https://login.test");

    private static readonly Guid Tenant = Guid.Parse("aa000000-0000-0000-0000-000000000001");

    private static readonly string Recent = DateTimeOffset.UtcNow.AddDays(-1).ToString("o");
    private static readonly string Old = DateTimeOffset.UtcNow.AddDays(-120).ToString("o");

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

    // ---- 2) Paginação por @odata.nextLink ----------------------------------------------------------

    [Fact]
    public async Task GraphClient_FollowsPagination()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("p=2")) return (HttpStatusCode.OK, """{"value":[{"id":"2"}]}""");
            if (url.EndsWith("/v1.0/foo")) return (HttpStatusCode.OK, """{"value":[{"id":"1"}],"@odata.nextLink":"https://graph.test/v1.0/foo?p=2"}""");
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

    // ---- HTTP simulado -----------------------------------------------------------------------------

    private static HttpMessageHandler HappyHandler() => new StubHandler(Happy);

    /// <summary>Respostas "felizes" do Graph (token + 6 capacidades) para o cenário de coleta completa.</summary>
    private static (HttpStatusCode, string) Happy(HttpRequestMessage req)
    {
        var url = req.RequestUri!.AbsoluteUri;

        if (req.Method == HttpMethod.Post && url.Contains("/oauth2/v2.0/token"))
            return (HttpStatusCode.OK, """{"access_token":"fake-access-token","expires_in":3600,"token_type":"Bearer"}""");

        if (url.Contains("/directoryRoles/") && url.Contains("/members"))
            return (HttpStatusCode.OK,
                """{"value":[{"id":"u1","userType":"Member","mail":"admin@x.test","signInActivity":{"lastSignInDateTime":"__RECENT__"}},{"id":"u2","userType":"Guest"}]}"""
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
