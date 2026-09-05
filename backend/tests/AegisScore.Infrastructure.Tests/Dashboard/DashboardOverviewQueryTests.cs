using AegisScore.Application.Abstractions;
using AegisScore.Application.Identity;
using AegisScore.Application.Knight;
using AegisScore.Application.Queries;
using AegisScore.Application.Scoring;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Queries;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Dashboard;

/// <summary>
/// [AEGIS-MVP-PRODUCT-01] Prova a INVARIANTE central da tela inicial: cada dimensão tem estado PRÓPRIO.
///
/// O defeito corrigido: o painel decidia "tem postura?" por maturidade CMMI + registro de riscos legado, e
/// num ambiente com telemetria REAL (ativos, exposições, vulnerabilidades, identidade) porém sem assessment
/// de maturidade a tela inteira exibia "Nenhuma postura medida" — escondendo tudo o que já havia sido
/// observado. Aqui isso é impossível por contrato: <c>Environment</c> e <c>BusinessRisk</c> são blocos
/// independentes, e a ausência de um NÃO zera nem esconde o outro.
///
/// A segunda invariante: ausência de coleta vira valor NULO, jamais zero. "0 vulnerabilidades abertas" só
/// pode existir depois de uma coleta real — do contrário leria como "nenhum problema".
///
/// Composição sobre SQLite in-memory para as contagens reais (ativos, maturidade, riscos, ICR) + dublês para
/// as autoridades de leitura já cobertas pelos próprios testes (postura, exposições, vulnerabilidades,
/// identidade): o alvo aqui é a COMPOSIÇÃO, não recalcular o que já tem dono.
/// </summary>
public sealed class DashboardOverviewQueryTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly SqliteConnection _connection;

    public DashboardOverviewQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
        ctx.Tenants.Add(new Tenant { Id = TenantId, Name = "AEGIS Homolog", Slug = "aegis-homolog" });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task ComTelemetriaESemMaturidade_OAmbienteObservadoContinuaVisivel()
    {
        await using (var seed = NewContext())
        {
            seed.Assets.AddRange(
                NewAsset("Servidor de arquivos"),
                NewAsset("Estação da diretoria"));
            await seed.SaveChangesAsync();
        }

        await using var db = NewContext();
        var overview = await QueryFor(db,
            exposures: CollectedExposures(totalOpen: 12),
            vulnerabilities: CollectedVulnerabilities(distinctCves: 37, affectedAssets: 2),
            identity: PartialIdentity()).GetAsync();

        // A dimensão de NEGÓCIO está honestamente vazia…
        overview.BusinessRisk.MaturityState.Should().Be(DashboardSignalState.NeverCollected);
        overview.BusinessRisk.OverallMaturity.Should().BeNull("sem avaliação não há maturidade — nem 0");
        overview.BusinessRisk.IcrState.Should().Be(DashboardSignalState.NeverCollected);
        overview.BusinessRisk.CriticalProcessesExposed.Should().BeNull();

        // …e isso NÃO apaga nada do que o ambiente já entregou.
        overview.Environment.Assets.State.Should().Be(DashboardSignalState.Available);
        overview.Environment.Assets.Value.Should().Be(2);
        overview.Environment.ConfigurationExposures.Value.Should().Be(12);
        overview.Environment.Vulnerabilities.Value.Should().Be(37);
        overview.Environment.AffectedAssets.Value.Should().Be(2);
        overview.Environment.Identity.State.Should().Be(DashboardSignalState.Partial,
            "coleta parcial de identidade continua sendo evidência — não é integração sem dados");
    }

    [Fact]
    public async Task SemColetaAlguma_AsMetricasSaoNulas_NuncaZero()
    {
        await using var db = NewContext();
        var overview = await QueryFor(db,
            exposures: NeverCollectedExposures(),
            vulnerabilities: NeverCollectedVulnerabilities(),
            identity: NoIdentitySource()).GetAsync();

        overview.Environment.Assets.Value.Should().BeNull();
        overview.Environment.Assets.State.Should().Be(DashboardSignalState.NeverCollected);
        overview.Environment.ConfigurationExposures.Value.Should().BeNull(
            "'0 exposições' leria como 'nenhum problema' num ambiente que nunca foi lido");
        overview.Environment.Vulnerabilities.Value.Should().BeNull();
        overview.Environment.AffectedAssets.Value.Should().BeNull();
        overview.Environment.Identity.State.Should().Be(DashboardSignalState.NoSource);

        // Cada métrica identifica a PRÓPRIA origem, mesmo vazia.
        overview.Environment.ConfigurationExposures.SourceLabel.Should().NotBeNullOrWhiteSpace();
        overview.Environment.ConfigurationExposures.Note.Should().NotBeNullOrWhiteSpace(
            "o estado vazio explica por que está vazio");
    }

    [Fact]
    public async Task ComIcrMedidoESemMaturidade_CadaSubDimensaoMantemOProprioEstado()
    {
        await using (var seed = NewContext())
        {
            seed.IcrScores.Add(new IcrScore { TenantId = TenantId, Score = 72 });
            await seed.SaveChangesAsync();
        }

        await using var db = NewContext();
        var overview = await QueryFor(db).GetAsync();

        overview.BusinessRisk.IcrState.Should().Be(DashboardSignalState.Available);
        overview.BusinessRisk.IcrScore.Should().Be(72);
        overview.BusinessRisk.IcrBand.Should().NotBeNullOrWhiteSpace();
        overview.BusinessRisk.MaturityState.Should().Be(DashboardSignalState.NeverCollected,
            "ICR medido não implica maturidade avaliada — são dimensões distintas");
        overview.BusinessRisk.OverallMaturity.Should().BeNull();
    }

    [Fact]
    public async Task FonteHabilitadaQueNuncaSincronizou_EntraNaContagemDeAtencao()
    {
        await using var db = NewContext();
        var overview = await QueryFor(db, connectors: new ConnectorHealthSummaryDto(
            Configured: 2, Enabled: 2, Disabled: 0, Healthy: 1, Degraded: 0, Failed: 0, NeverSynced: 1,
            LastSyncAt: Now.AddDays(-1),
            Items: new[]
            {
                new ConnectorHealthItemDto(Guid.NewGuid(), "Entra ID", "Microsoft", "IdentityPosture",
                    "Healthy", Now.AddDays(-1), true, true),
                new ConnectorHealthItemDto(Guid.NewGuid(), "Defender", "Microsoft", "VulnerabilityScanner",
                    "Unknown", null, false, true),
            })).GetAsync();

        overview.Sources.Attention.Should().Be(1);
        overview.Sources.Items.Should().HaveCount(2);
        overview.Sources.Items[0].EverSynced.Should().BeFalse(
            "a ordenação leva ao topo a fonte que precisa de atenção");
        overview.Sources.Items[0].StaleDays.Should().BeNull("nunca sincronizou — não há idade a informar");
    }

    [Fact]
    public async Task FonteSaudavelPorémAntiga_ContaComoAtencao_SemAlterarOsContadoresDaAutoridade()
    {
        await using var db = NewContext();
        var stale = Now.AddDays(-(DashboardOverviewDto.StaleAfterDays + 3));

        var overview = await QueryFor(db, connectors: new ConnectorHealthSummaryDto(
            Configured: 1, Enabled: 1, Disabled: 0, Healthy: 1, Degraded: 0, Failed: 0, NeverSynced: 0,
            LastSyncAt: stale,
            Items: new[]
            {
                new ConnectorHealthItemDto(Guid.NewGuid(), "Secure Score", "Microsoft", "SecureScore",
                    "Healthy", stale, true, true),
            })).GetAsync();

        overview.Sources.Attention.Should().Be(1, "leitura antiga é motivo de atenção na tela");
        overview.Sources.Healthy.Should().Be(1, "os contadores da autoridade são copiados VERBATIM");
        overview.Sources.Items[0].StaleDays.Should().Be(DashboardOverviewDto.StaleAfterDays + 3);
    }

    // ---- infraestrutura do teste ----------------------------------------------------

    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private AegisScoreDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(TenantId));

    private static Asset NewAsset(string name) => new()
    {
        TenantId = TenantId,
        Name = name,
        Category = AssetCategory.Hardware,
        Criticality = 3,
        IsActive = true,
    };

    private static DashboardOverviewQuery QueryFor(
        AegisScoreDbContext db,
        PostureExposureListDto? exposures = null,
        VulnerabilityOverviewDto? vulnerabilities = null,
        IdentityEvidenceProjection? identity = null,
        ConnectorHealthSummaryDto? connectors = null) =>
        new(db,
            new SystemTenantContext(TenantId),
            new FakePosture(connectors ?? EmptyConnectors),
            new FakeExposures(exposures ?? NeverCollectedExposures()),
            new FakeVulnerabilities(vulnerabilities ?? NeverCollectedVulnerabilities()),
            new FakeIdentity(identity ?? NoIdentitySource()),
            new MaturityScoringService(),
            new IcrScoringService(),
            new FixedClock(Now));

    private static readonly ConnectorHealthSummaryDto EmptyConnectors =
        new(0, 0, 0, 0, 0, 0, 0, null, Array.Empty<ConnectorHealthItemDto>());

    private static readonly EvidenceCoverageSliceDto EmptySlice = new(0, 0, 0, 0, 0);

    private static PostureExposureSummaryDto ExposureSummary(int totalOpen, DateTimeOffset? collectedAt) =>
        new("Microsoft Secure Score", totalOpen, 0, Array.Empty<PostureExposureCategoryCountDto>(), collectedAt, null, null);

    private static PostureExposureListDto NeverCollectedExposures() =>
        new(ExposureSummary(0, null), Array.Empty<PostureExposureItemDto>(), 0, 1, 4);

    private static PostureExposureListDto CollectedExposures(int totalOpen) =>
        new(ExposureSummary(totalOpen, Now.AddHours(-6)), Array.Empty<PostureExposureItemDto>(), totalOpen, 1, 4);

    private static VulnerabilitySummaryDto VulnerabilitySummary(
        int distinctCves, int affectedAssets, bool neverCollected) =>
        new(distinctCves, 0, distinctCves, affectedAssets,
            Array.Empty<VulnerabilitySeverityCountDto>(), Array.Empty<VulnerabilitySourceDto>(),
            neverCollected ? null : Now.AddHours(-3), neverCollected);

    private static VulnerabilityOverviewDto NeverCollectedVulnerabilities() =>
        new(VulnerabilitySummary(0, 0, neverCollected: true), Array.Empty<VulnerabilityGroupDto>(), 0, 1, 4);

    private static VulnerabilityOverviewDto CollectedVulnerabilities(int distinctCves, int affectedAssets) =>
        new(VulnerabilitySummary(distinctCves, affectedAssets, neverCollected: false),
            Array.Empty<VulnerabilityGroupDto>(), distinctCves, 1, 4);

    private static IdentityEvidenceProjection NoIdentitySource() => new(
        IdentityEvidenceConnectorState.NotConfigured, IdentityEvidenceCollectionState.NoConnector,
        KnightSourceState.NotConfigured, false, "Microsoft Entra ID", null, null, null, null,
        Array.Empty<IdentityCapabilityView>(), Array.Empty<IdentityControlEvidence>());

    private static IdentityEvidenceProjection PartialIdentity() => new(
        IdentityEvidenceConnectorState.Configured, IdentityEvidenceCollectionState.Partial,
        KnightSourceState.PartialCollection, false, "Microsoft Entra ID", "v2", Now.AddHours(-2), Now.AddHours(-2), null,
        new[]
        {
            new IdentityCapabilityView(KnightCapability.MfaRegistration, KnightCapabilityOutcome.Collected, null),
            new IdentityCapabilityView(KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Collected, null),
            new IdentityCapabilityView(KnightCapability.GuestAccounts, KnightCapabilityOutcome.InsufficientPermission,
                "Permissão adicional não concedida no tenant."),
        },
        new[]
        {
            new IdentityControlEvidence("PR.AA-01", "Identidades e credenciais gerenciadas",
                IdentityControlEvidenceState.CollectedButInsufficient, "Telemetria presente, evidência insuficiente."),
        });

    private sealed class FakePosture : IWorkspacePostureQuery
    {
        private readonly ConnectorHealthSummaryDto _connectors;
        public FakePosture(ConnectorHealthSummaryDto connectors) => _connectors = connectors;

        public Task<WorkspacePostureDto> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new WorkspacePostureDto(
                new WorkspaceOverallDto("aegis-score-v1", "NotEvaluated", null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    Array.Empty<SeverityCountDto>(), null),
                Array.Empty<FunctionPostureDto>(),
                _connectors,
                new EvidenceCoverageSummaryDto(EmptySlice, EmptySlice, EmptySlice, EmptySlice)));
    }

    private sealed class FakeExposures : IPostureExposureQuery
    {
        private readonly PostureExposureListDto _result;
        public FakeExposures(PostureExposureListDto result) => _result = result;
        public Task<PostureExposureListDto> GetAsync(PostureExposureFilter filter, CancellationToken ct = default) =>
            Task.FromResult(_result);
    }

    private sealed class FakeVulnerabilities : IVulnerabilityQuery
    {
        private readonly VulnerabilityOverviewDto _overview;
        public FakeVulnerabilities(VulnerabilityOverviewDto overview) => _overview = overview;

        public Task<VulnerabilityListDto> GetAsync(VulnerabilityFilter filter, CancellationToken ct = default) =>
            throw new InvalidOperationException("A tela inicial usa a visão AGRUPADA, nunca a de ocorrências.");

        public Task<VulnerabilityOverviewDto> GetOverviewAsync(VulnerabilityFilter filter, CancellationToken ct = default) =>
            Task.FromResult(_overview);
    }

    private sealed class FakeIdentity : IIdentityEvidenceService
    {
        private readonly IdentityEvidenceProjection _projection;
        public FakeIdentity(IdentityEvidenceProjection projection) => _projection = projection;

        public Task<IdentityEvidenceAcquisition> CollectAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("A tela inicial NUNCA aciona coleta externa.");

        public Task<IdentityEvidenceProjection> GetLatestProjectionAsync(CancellationToken ct = default) =>
            Task.FromResult(_projection);
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
