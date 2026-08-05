using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Connectors.Google;
using AegisScore.Domain;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// Testes FOCADOS (6) do coletor REAL do Google Workspace por HTTP SIMULADO (sem rede, sem credenciais reais).
/// A autenticação da service account é uma PORTA fakeada — os testes exercitam o protocolo do Admin SDK/Reports
/// (paginação por nextPageToken, normalização de usuários/2SV, 403 → NotEvaluated) sem tocar rede/criptografia.
/// </summary>
public sealed class GoogleWorkspaceCollectorTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly KnightGoogleWorkspaceConfiguration Cfg = new(
        CustomerId: "C0test123",
        DelegatedAdminEmail: "admin@org.example.com",
        ServiceAccountJson: """{"type":"service_account","private_key":"PRIVATE-KEY-MARKER"}""");

    private static readonly Guid Tenant = Guid.Parse("aa000000-0000-0000-0000-000000000001");
    private static readonly string Recent = DateTimeOffset.UtcNow.AddDays(-1).ToString("o");

    private readonly SqliteConnection _connection;

    public GoogleWorkspaceCollectorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---- 1) Configuração Google cifrada é resolvida para o tenant correto --------------------------

    [Fact]
    public async Task Config_GoogleEncryptedSettings_ResolvedForCorrectTenant()
    {
        var json = """{"customerId":"C0abc","delegatedAdminEmail":"admin@org.example.com","serviceAccountJson":"{\"type\":\"service_account\",\"private_key\":\"KEY\"}"}""";
        await using (var seed = NewContext(TenantA))
        {
            // O ConnectorConfig tem FK para Tenant — cria a linha do tenant antes.
            seed.Set<Tenant>().Add(new Tenant { Id = TenantA, Name = "Org A", Slug = "org-a", Status = TenantStatus.Active });
            seed.Connectors.Add(new ConnectorConfig
            {
                TenantId = TenantA,
                Provider = ConnectorProvider.Google,
                Capability = ConnectorCapability.IdentityPosture,
                AuthType = ConnectorAuthType.ServiceAccount,
                DisplayName = "Google KNIGHT",
                EncryptedSettings = json,
                Enabled = true,
            });
            await seed.SaveChangesAsync();
        }

        await using (var ctxA = NewContext(TenantA))
        {
            var resolved = await new KnightSourceConfigurationProvider(ctxA, new IdentityProtector())
                .ResolveAsync(TenantA, KnightSourceType.GoogleWorkspace);
            resolved.Should().BeOfType<KnightGoogleWorkspaceConfiguration>();
            var g = (KnightGoogleWorkspaceConfiguration)resolved;
            g.CustomerId.Should().Be("C0abc");
            g.DelegatedAdminEmail.Should().Be("admin@org.example.com");
            g.ServiceAccountJson.Should().Contain("private_key");
        }

        await using (var ctxB = NewContext(TenantB))
        {
            (await new KnightSourceConfigurationProvider(ctxB, new IdentityProtector())
                .ResolveAsync(TenantB, KnightSourceType.GoogleWorkspace))
                .Should().BeOfType<KnightSourceNotConfigured>("o conector é de outro tenant (isolado pelo query filter)");
        }
    }

    // ---- 2) Coleta normalizada de usuários/2SV -----------------------------------------------------

    [Fact]
    public async Task Collector_NormalizesUsersAnd2Sv()
    {
        var collector = new GoogleWorkspaceKnightCollector(new FakeAuth("fake-google-token"),
            new GoogleWorkspaceApiClient(new HttpClient(new StubHandler(HappyGoogle))));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        result.Source.Should().Be(KnightSourceType.GoogleWorkspace);
        // 3 usuários ativos, 1 com 2SV → 33,3%; 1 superadmin sem 2SV.
        result.Facts.Get(KnightSignalKey.TwoStepVerificationCoveragePercent).Ratio.Should().BeApproximately(33.3, 0.2);
        result.Facts.Get(KnightSignalKey.SuperAdminsTotal).Count.Should().Be(1);
        result.Facts.Get(KnightSignalKey.SuperAdminsWithout2Sv).Count.Should().Be(1);

        KnightIndicatorEvaluator.Evaluate(result.Facts, KnightSourceType.GoogleWorkspace)
            .Single(i => i.Definition.Id == "AK-GWS-001").Status.Should().Be(KnightIndicatorStatus.Exposed);
    }

    // ---- 3) Paginação por nextPageToken (e teto fail-closed) ---------------------------------------

    [Fact]
    public async Task ApiClient_FollowsNextPageToken_AndFailsClosedOnCap()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("pageToken=tok2")) return (HttpStatusCode.OK, """{"users":[{"id":"2"}]}""");
            if (url.Contains("/users")) return (HttpStatusCode.OK, """{"users":[{"id":"1"}],"nextPageToken":"tok2"}""");
            return (HttpStatusCode.NotFound, "{}");
        });
        var client = new GoogleWorkspaceApiClient(new HttpClient(handler));

        var ids = new List<string>();
        await foreach (var u in client.GetPagedAsync("tok", "admin/directory/v1/users?customer=C0test123", "users", CancellationToken.None))
            ids.Add(u.GetProperty("id").GetString()!);
        ids.Should().Equal("1", "2");

        // Sempre devolve nextPageToken → com teto 1, a página seguinte é recusada (não trunca em silêncio).
        var infinite = new GoogleWorkspaceApiClient(
            new HttpClient(new StubHandler(_ => (HttpStatusCode.OK, """{"users":[{"id":"x"}],"nextPageToken":"more"}"""))), maxPages: 1);
        var act = async () =>
        {
            await foreach (var _ in infinite.GetPagedAsync("tok", "admin/directory/v1/users?customer=C0test123", "users", CancellationToken.None)) { }
        };
        await act.Should().ThrowAsync<GoogleWorkspaceException>();
    }

    // ---- 4) Permissão insuficiente → NotEvaluated --------------------------------------------------

    [Fact]
    public async Task Collector_InsufficientPermission_YieldsNotEvaluated()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("/directory/v1/users")) return (HttpStatusCode.Forbidden, """{"error":{"code":403}}""");
            return HappyGoogle(req);
        });
        var collector = new GoogleWorkspaceKnightCollector(new FakeAuth("fake-google-token"),
            new GoogleWorkspaceApiClient(new HttpClient(handler)));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        result.State.Should().Be(KnightSourceState.PartialCollection);
        result.Capabilities.Should().Contain(c =>
            c.Capability == KnightCapability.DirectoryUsers && c.Outcome == KnightCapabilityOutcome.InsufficientPermission);
        result.Facts.Get(KnightSignalKey.TwoStepVerificationCoveragePercent).Outcome.Should().Be(KnightObservationOutcome.Missing);
        KnightIndicatorEvaluator.Evaluate(result.Facts, KnightSourceType.GoogleWorkspace)
            .Single(i => i.Definition.Id == "AK-GWS-002").Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
    }

    // ---- 5) Segredo (chave privada) e token não vazam no resultado; ToString redige o segredo ------

    [Fact]
    public async Task Collector_SecretAndToken_NotExposed()
    {
        var collector = new GoogleWorkspaceKnightCollector(new FakeAuth("fake-google-token"),
            new GoogleWorkspaceApiClient(new HttpClient(new StubHandler(HappyGoogle))));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));
        var serialized = JsonSerializer.Serialize(result);

        serialized.Should().NotContain("PRIVATE-KEY-MARKER", "a chave privada da service account nunca aparece no resultado");
        serialized.Should().NotContain("fake-google-token", "o access token nunca aparece no resultado");
        Cfg.ToString().Should().NotContain("PRIVATE-KEY-MARKER", "o ToString do config redige o JSON da service account");
        Cfg.ToString().Should().Contain("***");
    }

    // ---- 6) Falha REAL de autenticação NÃO cai para Demo -------------------------------------------

    [Fact]
    public async Task Collector_AuthFailure_ReturnsRealState_NotDemo()
    {
        var collector = new GoogleWorkspaceKnightCollector(
            new FakeAuth(new GoogleWorkspaceException(GoogleWorkspaceErrorKind.AuthFailure, "sanitizada")),
            new GoogleWorkspaceApiClient(new HttpClient(new StubHandler(HappyGoogle))));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        result.State.Should().Be(KnightSourceState.AuthenticationFailure);
        result.Source.Should().Be(KnightSourceType.GoogleWorkspace);
        result.SourceLabel.Should().NotContain("Demo");
        result.Facts.All.Should().BeEmpty("uma falha de autenticação não produz fatos sintéticos");
    }

    // ---- HTTP simulado + fakes ---------------------------------------------------------------------

    /// <summary>Respostas "felizes" do Admin SDK/Reports para as 4 capacidades (state Completed).</summary>
    private static (HttpStatusCode, string) HappyGoogle(HttpRequestMessage req)
    {
        var url = req.RequestUri!.AbsoluteUri;

        // Reports primeiro: seus caminhos (activity/users/all/...) TAMBÉM contêm "users".
        if (url.Contains("applications/drive"))
            return (HttpStatusCode.OK, """{"items":[]}""");

        if (url.Contains("applications/token"))
            return (HttpStatusCode.OK, """{"items":[]}""");

        if (url.Contains("/directory/v1/users"))
            return (HttpStatusCode.OK,
                """{"users":[{"isAdmin":true,"isEnrolledIn2Sv":false,"suspended":false,"archived":false,"lastLoginTime":"__RECENT__"},{"isAdmin":false,"isEnrolledIn2Sv":true},{"isAdmin":false,"isEnrolledIn2Sv":false}]}"""
                    .Replace("__RECENT__", Recent));

        if (url.Contains("/domains"))
            return (HttpStatusCode.OK, """{"domains":[{"domainName":"org.example.com"}]}""");

        if (url.Contains("/groups/") && url.Contains("/members"))
            return (HttpStatusCode.OK, """{"members":[{"email":"ext@other.example.com","type":"USER"},{"email":"inside@org.example.com","type":"USER"}]}""");

        if (url.Contains("/groups"))
            return (HttpStatusCode.OK, """{"groups":[{"id":"g1"}]}""");

        return (HttpStatusCode.NotFound, "{}");
    }

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    private sealed class IdentityProtector : IConnectorSecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class FakeAuth : IGoogleWorkspaceAuthenticator
    {
        private readonly string? _token;
        private readonly GoogleWorkspaceException? _throw;
        public FakeAuth(string token) => _token = token;
        public FakeAuth(GoogleWorkspaceException ex) => _throw = ex;
        public Task<string> AcquireAccessTokenAsync(KnightGoogleWorkspaceConfiguration config, CancellationToken ct) =>
            _throw is not null ? throw _throw : Task.FromResult(_token!);
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
