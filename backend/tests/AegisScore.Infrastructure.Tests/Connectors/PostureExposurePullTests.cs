using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Scoring;
using AegisScore.Connectors.Microsoft;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Scoring;
using AegisScore.Infrastructure.Tests.Documents;   // PostgresProbe
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Connectors;

/// <summary>
/// [AEGIS-MVP-POSTURE-02] Cadeia PULL ponta a ponta do Secure Score REAL (HTTP simulado): o executor coleta os
/// sinais (que passam pela autoridade central <see cref="NistSignalMapper"/>) E, no MESMO fluxo, coleta e
/// reconcilia as exposições — o adaptador nunca escreve no banco. Bateria relacional (SQLite) + uma bateria
/// PostgreSQL real gated por <c>AEGIS_TEST_PG</c> para migration/unicidade/reconciliação.
/// </summary>
public sealed class PostureExposurePullTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string AzureTenantId = "11111111-2222-3333-4444-555555555555";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;
    private Guid _connectorId;

    public PostureExposurePullTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;

        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
        SecureScoreTestData.SeedFrameworkAndMappings(ctx);
        ctx.Tenants.Add(new Tenant { Id = Tenant, Name = "Alfa", Slug = "alfa", Status = TenantStatus.Active });
        ctx.SaveChanges();
        _connectorId = SecureScoreTestData.SeedConnector(NewContext(Tenant), Tenant, AzureTenantId);
    }

    public void Dispose() => _connection.Dispose();

    private AegisScoreDbContext NewContext(Guid? tenant) => new(_options, new SystemTenantContext(tenant));

    private EvidenceIngestionExecutor MakeExecutor(IConnectorRegistry registry) => new(
        _options, new NistSignalMapper(NewContext(null)), new FakeProtector(), registry,
        NullLogger<EvidenceIngestionExecutor>.Instance, NullLogger<ControlStateWriter>.Instance);

    [Fact]
    public async Task Pull_SecureScore_MapsSignalsCentrally_AndReconcilesExposures()
    {
        var connector = new MicrosoftSecureScoreConnector(
            new EntraGraphClient(new HttpClient(SecureScoreTestData.HappyHandler(AzureTenantId))),
            new SecureScoreTestData.IdentityProtector());
        var exec = MakeExecutor(new FakeRegistry(connector));

        await using var read = NewContext(Tenant);
        var config = await read.Connectors.SingleAsync(c => c.Id == _connectorId);

        var result = await exec.CollectPullAsync(config, default);
        result.Should().NotBeNull();
        result!.Persisted.Should().Be(5, "overall + 4 categorias");

        await using var assert = NewContext(Tenant);

        // (12) Sinais passam pela autoridade central: o overall recebe os códigos do SignalMapping (não vazios).
        var overall = await assert.Signals.SingleAsync(s => s.SignalKey == "secureScore.overall");
        overall.MappedSubcategoryCodes.Should().BeEquivalentTo(new[] { "PR.AA-01", "PR.DS-01", "PR.PS-01" },
            "o NistSignalMapper é a autoridade — não os códigos do adaptador (que vêm vazios)");
        overall.Unit.Should().Be("percent");

        // Projeção determinística no ledger (todos < 80% → NonCompliant): prova que o pipeline de score rodou.
        var prAa01 = await assert.TenantControlStates.Include(x => x.Subcategory)
            .SingleAsync(x => x.Subcategory!.Code == "PR.AA-01");
        prAa01.Status.Should().Be(ControlStatus.NonCompliant, "54%/40% < 80% → NonCompliant");
        prAa01.LastVerdictSource.Should().Be(VerdictSource.Telemetry);

        // Exposições reconciliadas no MESMO fluxo pull (deprecated excluído).
        var findings = await assert.PostureExposureFindings.ToListAsync();
        findings.Should().HaveCount(5, "5 controles não-deprecated com gap positivo");
        findings.Should().OnlyContain(f => f.LifecycleState == PostureExposureState.Open);
        findings.Should().NotContain(f => f.ExternalId == "c-dep-1", "deprecated não cria exposição");
        findings.Select(f => f.ConnectorConfigId).Should().OnlyContain(id => id == _connectorId);

        // Conector saudável após a coleta.
        (await assert.Connectors.SingleAsync(c => c.Id == _connectorId)).LastStatus
            .Should().Be(ConnectorStatus.Healthy);
    }

    [Fact]
    public async Task Pull_SecondSync_ResolvesControlThatNoLongerHasGap()
    {
        // 1ª coleta: c-dev-1 tem gap (score 2/10). 2ª coleta: c-dev-1 agora completo (10/10) → sem gap → Resolved.
        var handler1 = SecureScoreTestData.HappyHandler(AzureTenantId);
        var exec1 = MakeExecutor(new FakeRegistry(new MicrosoftSecureScoreConnector(
            new EntraGraphClient(new HttpClient(handler1)), new SecureScoreTestData.IdentityProtector())));
        await using (var read = NewContext(Tenant))
            await exec1.CollectPullAsync(await read.Connectors.SingleAsync(c => c.Id == _connectorId), default);

        var handler2 = SecureScoreTestData.HandlerWithDeviceResolved(AzureTenantId);
        var exec2 = MakeExecutor(new FakeRegistry(new MicrosoftSecureScoreConnector(
            new EntraGraphClient(new HttpClient(handler2)), new SecureScoreTestData.IdentityProtector())));
        await using (var read = NewContext(Tenant))
            await exec2.CollectPullAsync(await read.Connectors.SingleAsync(c => c.Id == _connectorId), default);

        await using var assert = NewContext(Tenant);
        var dev = await assert.PostureExposureFindings.SingleAsync(f => f.ExternalId == "c-dev-1");
        dev.LifecycleState.Should().Be(PostureExposureState.Resolved, "sem gap na coleta completa → resolvido");
        dev.ResolvedAt.Should().NotBeNull();
        // Repetição não duplica.
        (await assert.PostureExposureFindings.CountAsync(f => f.ExternalId == "c-dev-1")).Should().Be(1);
    }
}

/// <summary>[AEGIS-MVP-POSTURE-02] Migration, unicidade e reconciliação em PostgreSQL real (gate <c>AEGIS_TEST_PG</c>).</summary>
public sealed class PostureExposurePostgresTests
{
    [Fact]
    public async Task Migration_Uniqueness_And_Reconciliation_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;   // AEGIS_TEST_PG não definido — pulado honestamente
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var connector = Guid.NewGuid();

        // (a) A MIGRATION aplica de fato no PostgreSQL (cria a tabela + o índice único natural).
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.Database.MigrateAsync();
            db.Tenants.Add(new Tenant { Id = tenant, Name = "T", Slug = "t-" + tenant.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }

        PostureFinding F(string id, double cur, double max) =>
            new(id, id, "Identity", "AAD", "Config", cur, max, max - cur, 1, "Core", "Low", "Low", "fix", "none", new[] { "T" }, null);

        // (b) RECONCILIAÇÃO idempotente + resolução em coleta completa.
        async Task Reconcile(bool complete, params PostureFinding[] f)
        {
            await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
            await new PostureExposureReconciler(db).ReconcileAsync(
                connector, new PostureFindingCollection(f, complete, "Microsoft Secure Score"), default);
        }

        await Reconcile(true, F("c1", 5, 10), F("c2", 3, 10));
        await Reconcile(true, F("c1", 5, 10), F("c2", 3, 10));   // idempotente
        await Reconcile(true, F("c2", 3, 10));                    // c1 sem gap → resolvido

        await using (var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await assert.PostureExposureFindings.CountAsync()).Should().Be(2, "sem duplicar; nada excluído");
            (await assert.PostureExposureFindings.SingleAsync(f => f.ExternalId == "c1")).LifecycleState
                .Should().Be(PostureExposureState.Resolved);
        }

        // (c) UNICIDADE como invariante de banco: inserir a MESMA chave natural falha.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            db.PostureExposureFindings.Add(new PostureExposureFinding { ConnectorConfigId = connector, ExternalId = "c2", Title = "dup" });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>("índice único (Tenant, Connector, ExternalId)");
        }
    }
}

/// <summary>Suporte de teste para o Secure Score REAL simulado (HTTP) + seed do framework com os mappings secureScore.*.</summary>
internal static class SecureScoreTestData
{
    public static Guid SeedConnector(AegisScoreDbContext db, Guid tenant, string azureTenantId)
    {
        using (db)
        {
            var cfg = new ConnectorConfig
            {
                TenantId = tenant, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.SecureScore,
                DisplayName = "Microsoft Secure Score", AuthType = ConnectorAuthType.OAuthClientCredentials, Enabled = true,
                EncryptedSettings =
                    $$"""{"tenantId":"{{azureTenantId}}","clientId":"app","clientSecret":"secret"}""",
            };
            db.Connectors.Add(cfg);
            db.SaveChanges();
            return cfg.Id;
        }
    }

    /// <summary>Framework mínimo com os 11 controles que os 5 sinais secureScore.* endereçam + os 5 SignalMappings.</summary>
    public static void SeedFrameworkAndMappings(AegisScoreDbContext db)
    {
        var fv = new FrameworkVersion { Name = "NIST CSF 2.0", IsActive = true };

        var pr = new NistFunction { FrameworkVersionId = fv.Id, Code = "PR", Name = "Protect" };
        pr.Categories.Add(Cat(pr, "PR.AA", "PR.AA-01", "PR.AA-03", "PR.AA-05"));
        pr.Categories.Add(Cat(pr, "PR.DS", "PR.DS-01", "PR.DS-02", "PR.DS-10"));
        pr.Categories.Add(Cat(pr, "PR.PS", "PR.PS-01", "PR.PS-05", "PR.PS-06"));

        var de = new NistFunction { FrameworkVersionId = fv.Id, Code = "DE", Name = "Detect" };
        var deCm = Cat(de, "DE.CM", "DE.CM-01", "DE.CM-09");
        de.Categories.Add(deCm);

        fv.Functions.Add(pr);
        fv.Functions.Add(de);
        db.FrameworkVersions.Add(fv);

        db.SignalMappings.AddRange(
            Map(fv.Id, "secureScore.overall", "PR.AA-01", "PR.DS-01", "PR.PS-01"),
            Map(fv.Id, "secureScore.identity", "PR.AA-01", "PR.AA-03", "PR.AA-05"),
            Map(fv.Id, "secureScore.data", "PR.DS-01", "PR.DS-02", "PR.DS-10"),
            Map(fv.Id, "secureScore.device", "PR.PS-01", "PR.PS-05", "DE.CM-01"),
            Map(fv.Id, "secureScore.apps", "PR.PS-06", "DE.CM-09"));
        db.SaveChanges();
    }

    private static NistCategory Cat(NistFunction fn, string code, params string[] subs)
    {
        var cat = new NistCategory { FunctionId = fn.Id, Code = code, Name = code };
        foreach (var s in subs)
            cat.Subcategories.Add(new NistSubcategory { CategoryId = cat.Id, Code = s, Description = s, MaxScorePoints = 10 });
        return cat;
    }

    private static SignalMapping Map(Guid fvId, string key, params string[] codes) => new()
    {
        FrameworkVersionId = fvId, Capability = ConnectorCapability.SecureScore, SignalKey = key,
        SubcategoryCodes = codes.ToList(), ScoringHint = EvidenceSignalEvaluator.PercentHigherIsBetter,
    };

    // ---- HTTP simulado ----
    private const string TokenJson = """{"access_token":"fake-token","expires_in":3600,"token_type":"Bearer"}""";

    public static HttpMessageHandler HappyHandler(string azureTenantId) =>
        new StubHandler(azureTenantId, deviceScore: 2);

    public static HttpMessageHandler HandlerWithDeviceResolved(string azureTenantId) =>
        new StubHandler(azureTenantId, deviceScore: 10);   // c-dev-1 completo → sem gap

    private static string ScoreJson(string azureTenantId, double deviceScore)
    {
        var controls =
            """{"controlName":"c-id-1","controlCategory":"Identity","score":5},""" +
            """{"controlName":"c-id-2","controlCategory":"Identity","score":3},""" +
            """{"controlName":"c-data-1","controlCategory":"Data","score":7},""" +
            $$"""{"controlName":"c-dev-1","controlCategory":"Device","score":{{deviceScore}}},""" +
            """{"controlName":"c-apps-1","controlCategory":"Apps","score":4},""" +
            """{"controlName":"c-dep-1","controlCategory":"Identity","score":1}""";
        return $$"""{"value":[{"azureTenantId":"{{azureTenantId}}","currentScore":54,"maxScore":100,"createdDateTime":"2026-08-20T10:00:00Z","controlScores":[{{controls}}]}]}""";
    }

    private static string ProfilesJson()
    {
        static string P(string id, string cat, int rank, bool dep = false) =>
            $$"""{"id":"{{id}}","title":"{{id}}","controlCategory":"{{cat}}","service":"AAD","maxScore":10,"rank":{{rank}},"tier":"Core","implementationCost":"Low","userImpact":"Low","actionType":"Config","remediation":"fix","remediationImpact":"none","threats":["Account Breach"],"deprecated":{{(dep ? "true" : "false")}},"controlStateUpdates":[]}""";
        var profiles = new[]
        {
            P("c-id-1", "Identity", 1), P("c-id-2", "Identity", 2), P("c-data-1", "Data", 3),
            P("c-dev-1", "Device", 4), P("c-apps-1", "Apps", 5), P("c-dep-1", "Identity", 6, dep: true),
        };
        return $$"""{"value":[{{string.Join(",", profiles)}}]}""";
    }

    public sealed class IdentityProtector : IConnectorSecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _azureTenantId;
        private readonly double _deviceScore;
        public StubHandler(string azureTenantId, double deviceScore)
        {
            _azureTenantId = azureTenantId;
            _deviceScore = deviceScore;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            string body;
            if (request.Method == HttpMethod.Post && url.Contains("/oauth2/v2.0/token")) body = TokenJson;
            else if (url.Contains("secureScores")) body = ScoreJson(_azureTenantId, _deviceScore);
            else if (url.Contains("secureScoreControlProfiles")) body = ProfilesJson();
            else return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
