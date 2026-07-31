using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Tests.Documents;   // PostgresProbe
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Connectors;

/// <summary>
/// [AEGIS-AUD-020/041/043] Ingestão genérica de evidências: autenticação pela chave do conector (boundary
/// cross-tenant), mapping determinístico via <see cref="SignalMapping"/> (nunca o LLM/adaptador), idempotência
/// e proteção do payload em repouso. Bateria relacional (SQLite) para a lógica determinística; a concorrência
/// real fica na bateria PostgreSQL (<see cref="EvidenceIngestionPostgresTests"/>).
/// </summary>
public sealed class EvidenceIngestionTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;
    private Guid _siemA, _edrA, _siemB;

    public EvidenceIngestionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;

        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
        IngestionTestData.SeedFrameworkAndMappings(ctx);
        ctx.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "Alfa", Slug = "alfa", Status = TenantStatus.Active },
            new Tenant { Id = TenantB, Name = "Bravo", Slug = "bravo", Status = TenantStatus.Active });
        ctx.SaveChanges();

        _siemA = SeedConnector(TenantA, ConnectorCapability.Siem, IngestionTestData.SiemKeyA);
        _edrA = SeedConnector(TenantA, ConnectorCapability.Edr, IngestionTestData.EdrKeyA);
        _siemB = SeedConnector(TenantB, ConnectorCapability.Siem, IngestionTestData.SiemKeyB);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Push_SiemValido_PersisteSobTenantCorreto_MapeadoEProtegido()
    {
        var connector = await Auth().AuthenticateAsync(_siemA, IngestionTestData.SiemKeyA, default);
        connector.Should().NotBeNull("chave válida do conector genérico SIEM");
        connector!.TenantId.Should().Be(TenantA, "o tenant vem SÓ do conector autenticado");

        var (exec, protector) = MakeExecutor();
        const string payload = "{\"rule\":\"segredo-tecnico\",\"srcIp\":\"192.0.2.10\"}";
        var result = await exec.IngestPushAsync(
            connector, IngestionTestData.Batch(IngestionTestData.SiemEvent(eventId: "e1", payload: payload)), default);

        result.Outcome.Should().Be(PushOutcome.Accepted);
        result.Accepted.Should().Be(1);
        result.Deduplicated.Should().Be(0);

        await using var assert = NewContext(TenantA);
        var signal = await assert.Signals.SingleAsync();
        signal.TenantId.Should().Be(TenantA);
        signal.ConnectorConfigId.Should().Be(_siemA);
        signal.MappedSubcategoryCodes.Should().BeEquivalentTo(new[] { "DE.AE-02", "DE.CM-01" },
            "o mapping determinístico (SignalMapping) é a autoridade");
        signal.ReceivedAt.Should().NotBeNull();
        signal.DeduplicationKey.Should().NotBeNullOrEmpty();

        signal.ProtectedRawPayload.Should().NotBeNullOrEmpty();
        signal.ProtectedRawPayload.Should().NotContain("segredo-tecnico", "o bruto não pode ficar legível no banco");
        protector.Unprotect(signal.ProtectedRawPayload!).Should().Be(payload, "protegido e recuperável (round-trip)");

        var cfg = await assert.Connectors.SingleAsync(c => c.Id == _siemA);
        cfg.LastStatus.Should().Be(ConnectorStatus.Healthy);
        cfg.LastSyncAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Push_EdrValido_Persiste_ComMappingEdr()
    {
        var connector = await Auth().AuthenticateAsync(_edrA, IngestionTestData.EdrKeyA, default);
        connector.Should().NotBeNull();

        var (exec, _) = MakeExecutor();
        var result = await exec.IngestPushAsync(connector!, IngestionTestData.Batch(IngestionTestData.EdrEvent()), default);

        result.Outcome.Should().Be(PushOutcome.Accepted);
        result.Accepted.Should().Be(1);

        await using var assert = NewContext(TenantA);
        var signal = await assert.Signals.SingleAsync();
        signal.MappedSubcategoryCodes.Should().BeEquivalentTo(new[] { "DE.CM-01", "RS.MI-01" });
        signal.ConnectorConfigId.Should().Be(_edrA);
    }

    [Theory]
    [InlineData("wrong-key")]     // conector existe, chave errada
    [InlineData("nonexistent")]   // conector inexistente
    [InlineData("cross-tenant")]  // chave de A apresentada ao conector de B
    public async Task Authenticate_InvalidaOuCrossTenant_Recusa_ZeroGravacoes(string scenario)
    {
        var (connectorId, key) = scenario switch
        {
            "wrong-key" => (_siemA, "chave-totalmente-errada-xxxxxxxx"),
            "nonexistent" => (Guid.NewGuid(), IngestionTestData.SiemKeyA),
            "cross-tenant" => (_siemB, IngestionTestData.SiemKeyA),
            _ => throw new InvalidOperationException(),
        };

        var connector = await Auth().AuthenticateAsync(connectorId, key, default);
        connector.Should().BeNull("conector inexistente/incompatível e chave inválida são indistinguíveis");

        await using var assert = NewContext(null);
        (await assert.Signals.IgnoreQueryFilters().CountAsync()).Should().Be(0, "nada é persistido sem autenticação");
    }

    [Fact]
    public async Task Push_SinalSemMapping_Recusa422_SemPersistir_SemLlm()
    {
        var connector = await Auth().AuthenticateAsync(_siemA, IngestionTestData.SiemKeyA, default);
        var (exec, _) = MakeExecutor();

        var result = await exec.IngestPushAsync(
            connector!, IngestionTestData.Batch(IngestionTestData.SiemEvent(signalKey: "siem.sinal.desconhecido")), default);

        result.Outcome.Should().Be(PushOutcome.Unmapped);
        result.Errors.Should().Contain("siem.sinal.desconhecido");

        await using var assert = NewContext(null);
        (await assert.Signals.IgnoreQueryFilters().CountAsync()).Should().Be(0, "sinal sem mapping não persiste nada");
        // "Zero LLM" é estrutural: o executor não injeta NENHUM serviço de IA/telemetria — não há como chamar.
    }

    [Theory]
    [InlineData(true)]    // idempotência por eventId
    [InlineData(false)]   // idempotência por hash de conteúdo (sem eventId)
    public async Task Push_Duplicata_SucessoIdempotente_UmaUnicaEvidencia(bool byEventId)
    {
        var connector = await Auth().AuthenticateAsync(_siemA, IngestionTestData.SiemKeyA, default);
        var (exec, _) = MakeExecutor();
        var ev = IngestionTestData.SiemEvent(eventId: byEventId ? "mesmo-id" : null);

        var r1 = await exec.IngestPushAsync(connector!, IngestionTestData.Batch(ev), default);
        var r2 = await exec.IngestPushAsync(connector!, IngestionTestData.Batch(ev), default);   // reenvio idêntico

        r1.Accepted.Should().Be(1);
        r2.Accepted.Should().Be(0);
        r2.Deduplicated.Should().Be(1);

        await using var assert = NewContext(TenantA);
        (await assert.Signals.CountAsync()).Should().Be(1, "duas requisições do mesmo evento = uma evidência");
    }

    [Fact]
    public async Task Pull_UsaMesmoExecutorEMapping_IgnorandoCodigosDoAdapter()
    {
        // Adaptador FAKE que emite um sinal SIEM com códigos ERRADOS — o executor deve SUBSTITUÍ-los pelo mapping.
        var fakeSignal = new EvidenceSignal
        {
            SignalKey = "siem.alert.highSeverity",
            NumericValue = 1,
            MappedSubcategoryCodes = new() { "ZZ.ZZ-99" },   // o adaptador "mente"
            CollectedAt = DateTimeOffset.UtcNow,
        };
        var (exec, _) = MakeExecutor(new FakeRegistry(
            new FakePullConnector(ConnectorProvider.Generic, ConnectorCapability.Siem, fakeSignal)));

        await using var read = NewContext(TenantA);
        var config = await read.Connectors.SingleAsync(c => c.Id == _siemA);

        var result = await exec.CollectPullAsync(config, default);
        result.Should().NotBeNull();
        result!.Persisted.Should().Be(1);

        await using var assert = NewContext(TenantA);
        var signal = await assert.Signals.SingleAsync();
        signal.MappedSubcategoryCodes.Should().BeEquivalentTo(new[] { "DE.AE-02", "DE.CM-01" },
            "o mapping central manda tanto no pull quanto no push");
        signal.MappedSubcategoryCodes.Should().NotContain("ZZ.ZZ-99", "os códigos do adaptador são ignorados");
    }

    // ---- Fixture ----------------------------------------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId) => new(_options, new SystemTenantContext(tenantId));

    private ConnectorIngestionAuthenticator Auth() => new(NewContext(null));

    private (EvidenceIngestionExecutor Exec, FakeProtector Protector) MakeExecutor(IConnectorRegistry? registry = null)
    {
        var protector = new FakeProtector();
        var exec = new EvidenceIngestionExecutor(
            _options, new NistSignalMapper(NewContext(null)), protector,
            registry ?? new FakeRegistry(), NullLogger<EvidenceIngestionExecutor>.Instance);
        return (exec, protector);
    }

    private Guid SeedConnector(Guid tenantId, ConnectorCapability capability, string key)
    {
        using var db = NewContext(tenantId);
        var cfg = new ConnectorConfig
        {
            TenantId = tenantId,
            Provider = ConnectorProvider.Generic,
            Capability = capability,
            DisplayName = $"Generic {capability}",
            AuthType = ConnectorAuthType.ApiKey,
            Enabled = true,
            IngestionKeyHash = IngestionKey.Hash(key),
        };
        db.Connectors.Add(cfg);
        db.SaveChanges();
        return cfg.Id;
    }
}

/// <summary>[AEGIS-AUD-020] Idempotência CONCORRENTE em PostgreSQL real (gate <c>AEGIS_TEST_PG</c>).</summary>
public sealed class EvidenceIngestionPostgresTests
{
    [Fact]
    public async Task Concurrent_MesmoEvento_UmaUnicaEvidencia_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;   // AEGIS_TEST_PG não definido — pulado
        var opt = pg.DbOptions();

        Guid tenant = Guid.NewGuid();
        Guid connectorId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.Database.EnsureCreatedAsync();
            IngestionTestData.SeedFrameworkAndMappings(db);
            db.Tenants.Add(new Tenant { Id = tenant, Name = "T", Slug = "t-" + tenant.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var cfg = new ConnectorConfig
            {
                TenantId = tenant, Provider = ConnectorProvider.Generic, Capability = ConnectorCapability.Siem,
                DisplayName = "Generic SIEM", AuthType = ConnectorAuthType.ApiKey, Enabled = true,
                IngestionKeyHash = IngestionKey.Hash(IngestionTestData.SiemKeyA),
            };
            db.Connectors.Add(cfg);
            await db.SaveChangesAsync();
            connectorId = cfg.Id;
        }

        var connector = new AuthenticatedConnector(connectorId, tenant, ConnectorCapability.Siem);

        // 8 "requisições" concorrentes com o MESMO evento (eventId). Cada uma com seu próprio executor/contexto.
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            var exec = new EvidenceIngestionExecutor(
                opt, new NistSignalMapper(new AegisScoreDbContext(opt, new SystemTenantContext(null))),
                new FakeProtector(), new FakeRegistry(), NullLogger<EvidenceIngestionExecutor>.Instance);
            return await exec.IngestPushAsync(
                connector, IngestionTestData.Batch(IngestionTestData.SiemEvent(eventId: "concorrente-1")), default);
        })).ToArray();

        var results = await Task.WhenAll(tasks);
        results.Sum(r => r.Accepted).Should().Be(1, "só UMA requisição grava a evidência");
        results.Sum(r => r.Deduplicated).Should().Be(7, "as demais são deduplicadas, não erro");

        await using var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        (await assert.Signals.CountAsync()).Should().Be(1, "uma única evidência para o mesmo evento");
    }
}

// ---- Suporte de teste (compartilhado pelas duas baterias) -----------------------------------------

internal static class IngestionTestData
{
    public const string SiemKeyA = "siem-key-A-aaaaaaaaaaaaaaaaaaaaaa";   // >= 24 chars
    public const string EdrKeyA = "edr-key-A-bbbbbbbbbbbbbbbbbbbbbbbb";
    public const string SiemKeyB = "siem-key-B-cccccccccccccccccccccc";

    public static EvidenceBatch Batch(params EvidenceEvent[] events) => new("1", events.ToList());

    public static EvidenceEvent SiemEvent(
        string? eventId = null, string signalKey = "siem.alert.highSeverity",
        string? payload = "{\"a\":1}", int? severity = 4) =>
        new(eventId, signalKey, "alert", "demo-siem", severity, 3, "count", FixedTime("2026-07-30T12:00:00Z"), payload);

    public static EvidenceEvent EdrEvent(string? eventId = "edr-1") =>
        new(eventId, "edr.threat.blocked", "detection", "demo-edr", 3, 1, "count", FixedTime("2026-07-30T12:05:00Z"), "{\"b\":2}");

    private static DateTimeOffset FixedTime(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    /// <summary>Semeia um framework MÍNIMO com os códigos usados nos sinais SIEM/EDR de referência + mappings.</summary>
    public static void SeedFrameworkAndMappings(AegisScoreDbContext db)
    {
        var fv = new FrameworkVersion { Name = "NIST CSF 2.0", IsActive = true };

        var de = new NistFunction { FrameworkVersionId = fv.Id, Code = "DE", Name = "Detect" };
        var deAe = new NistCategory { FunctionId = de.Id, Code = "DE.AE", Name = "Adverse Event Analysis" };
        deAe.Subcategories.Add(Sub(deAe.Id, "DE.AE-02"));
        var deCm = new NistCategory { FunctionId = de.Id, Code = "DE.CM", Name = "Continuous Monitoring" };
        deCm.Subcategories.Add(Sub(deCm.Id, "DE.CM-01"));
        de.Categories.Add(deAe);
        de.Categories.Add(deCm);

        var rs = new NistFunction { FrameworkVersionId = fv.Id, Code = "RS", Name = "Respond" };
        var rsMi = new NistCategory { FunctionId = rs.Id, Code = "RS.MI", Name = "Incident Mitigation" };
        rsMi.Subcategories.Add(Sub(rsMi.Id, "RS.MI-01"));
        rs.Categories.Add(rsMi);

        fv.Functions.Add(de);
        fv.Functions.Add(rs);
        db.FrameworkVersions.Add(fv);

        db.SignalMappings.AddRange(
            new SignalMapping
            {
                FrameworkVersionId = fv.Id, Capability = ConnectorCapability.Siem,
                SignalKey = "siem.alert.highSeverity", SubcategoryCodes = new() { "DE.AE-02", "DE.CM-01" },
            },
            new SignalMapping
            {
                FrameworkVersionId = fv.Id, Capability = ConnectorCapability.Edr,
                SignalKey = "edr.threat.blocked", SubcategoryCodes = new() { "DE.CM-01", "RS.MI-01" },
            });
        db.SaveChanges();
    }

    private static NistSubcategory Sub(Guid categoryId, string code) =>
        new() { CategoryId = categoryId, Code = code, Description = code, MaxScorePoints = 10 };
}

/// <summary>Protetor de payload FAKE: transforma (não é o plaintext) e faz round-trip — testável sem key ring.</summary>
internal sealed class FakeProtector : IEvidenceRawPayloadProtector
{
    private const string Prefix = "enc:";
    public string Protect(string plaintext) => Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext ?? ""));
    public string Unprotect(string protectedValue) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[Prefix.Length..]));
}

/// <summary>Registro de conectores FAKE para o caminho pull.</summary>
internal sealed class FakeRegistry : IConnectorRegistry
{
    private readonly List<IEvidenceConnector> _connectors;
    public FakeRegistry(params IEvidenceConnector[] connectors) => _connectors = connectors.ToList();
    public IReadOnlyList<IEvidenceConnector> All => _connectors;
    public IEvidenceConnector? Resolve(ConnectorProvider provider, ConnectorCapability capability) =>
        _connectors.FirstOrDefault(c => c.Provider == provider && c.Capability == capability);
}

/// <summary>Adaptador pull FAKE que emite sinais fixos (com códigos "errados", para provar que o executor re-mapeia).</summary>
internal sealed class FakePullConnector : IEvidenceConnector
{
    private readonly EvidenceSignal[] _signals;
    public FakePullConnector(ConnectorProvider provider, ConnectorCapability capability, params EvidenceSignal[] signals)
    {
        Provider = provider;
        Capability = capability;
        _signals = signals;
    }

    public ConnectorProvider Provider { get; }
    public ConnectorCapability Capability { get; }

    public Task<ConnectorHealth> TestAsync(ConnectorConfig config, CancellationToken ct) =>
        Task.FromResult(new ConnectorHealth(ConnectorStatus.Healthy, null));

    public async IAsyncEnumerable<EvidenceSignal> CollectAsync(
        ConnectorConfig config, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var s in _signals)
        {
            ct.ThrowIfCancellationRequested();
            yield return s;
        }
        await Task.CompletedTask;
    }
}
