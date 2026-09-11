using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Api.Controllers;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Identity;
using AegisScore.Application.Knight;
using AegisScore.Application.Queries;
using AegisScore.Application.Scoring;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Identity;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Queries;
using AegisScore.Infrastructure.Tests.Documents;   // PostgresProbe
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Dashboard;

/// <summary>
/// [AEGIS-MVP-PRODUCT-01] Validação INTEGRADA da leitura composta da tela inicial em PostgreSQL 18 REAL
/// (gate <c>AEGIS_TEST_PG</c>, banco descartável do <see cref="PostgresProbe"/>).
///
/// Por que ela existe: <c>DashboardOverviewQueryTests</c> prova a COMPOSIÇÃO sobre SQLite com dublês das
/// autoridades de leitura, e o smoke visual exercitou o SPA contra respostas simuladas. Nenhum dos dois
/// executa o SQL que este read model realmente emite no banco de produção. Aqui a query roda com as
/// DEPENDÊNCIAS REAIS — <see cref="WorkspacePostureQuery"/>, <see cref="PostureExposureQuery"/>,
/// <see cref="VulnerabilityQuery"/> e <see cref="IdentityEvidenceService"/> — sobre PostgreSQL, provando que
/// cada consulta TRADUZ (as armadilhas de EF do AEGIS_STATE §22.7 são exatamente deste tipo) e que a
/// projeção resultante é a correta para um tenant com evidência operacional e SEM maturidade legada.
///
/// LIMITE declarado com precisão: o alvo exercitado é a <b>query integrada</b> mais a <b>ação do controller</b>
/// (<see cref="DashboardController.Overview"/>) instanciada com essas dependências reais. O projeto não tem
/// harness HTTP de integração (não há <c>WebApplicationFactory</c>), então o <b>pipeline HTTP e a
/// autenticação JWT da rota <c>GET /api/v1/dashboard/overview</c> NÃO são executados</b> — criar esse harness
/// seria infraestrutura de teste nova, fora deste pacote. O que se pode garantir aqui sem ele é o CONTRATO de
/// exposição da rota (verba, template e ausência de <c>[AllowAnonymous]</c>, que a manteria fora da
/// FallbackPolicy autenticada do <c>Program.cs</c>) — verificado por reflexão, no mesmo padrão já usado nos
/// testes de atributo de autorização das superfícies de identidade.
///
/// Nenhuma chamada externa: Microsoft/Google jamais são acionados (o coletor de identidade usado na LEITURA
/// lança se alguém tentar coletar), e o PostgreSQL é o descartável do CI — nunca o corporativo.
/// </summary>
public sealed class DashboardOverviewPostgresTests
{
    private readonly ITestOutputHelper _output;

    public DashboardOverviewPostgresTests(ITestOutputHelper output) => _output = output;

    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ComposicaoReal_ComEvidenciaOperacionalESemMaturidade_ProjetaCadaDimensao_EIsolaOTenant()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;   // AEGIS_TEST_PG não definido — pulado honestamente
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.Database.MigrateAsync();
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Cliente Operacional", Slug = "op-" + tenant.ToString("N"), Status = TenantStatus.Active });
            db.Tenants.Add(new Tenant { Id = other, Name = "Outro Cliente", Slug = "ot-" + other.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }

        // ---- Tenant A: telemetria REAL (ativos, exposições, vulnerabilidades, identidade) e ZERO maturidade,
        //      ZERO ICR, ZERO registro de riscos — o cenário exato que a tela antiga apagava por inteiro.
        await SeedOperationalTenantAsync(opt, tenant);

        // ---- Tenant B: deliberadamente VAZIO, para provar que nada de A atravessa.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(other)))
        {
            db.Assets.Add(new Asset { TenantId = other, Name = "Ativo do outro cliente", Category = AssetCategory.Hardware, Criticality = 1, IsActive = true });
            await db.SaveChangesAsync();
        }

        // ================= LEITURA DO TENANT A, com dependências reais sobre PostgreSQL =================
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var overview = await RealQuery(db, tenant).GetAsync();

            overview.ClientName.Should().Be("Cliente Operacional");
            overview.ReadModelVersion.Should().Be(DashboardOverviewDto.Version);

            // (a) O ambiente OBSERVADO continua inteiro, mesmo sem nenhuma avaliação de negócio.
            overview.Environment.Assets.State.Should().Be(DashboardSignalState.Available);
            overview.Environment.Assets.Value.Should().Be(2, "dois ativos ATIVOS; o desativado não entra na contagem");

            overview.Environment.ConfigurationExposures.State.Should().Be(DashboardSignalState.Available);
            overview.Environment.ConfigurationExposures.Value.Should().Be(2, "duas exposições abertas; a resolvida não conta");
            overview.Environment.ConfigurationExposures.ObservedAt.Should().NotBeNull(
                "a recência vem do LastSyncAt do conector de Secure Score — é o que o cartão exibe como data");

            overview.Environment.Vulnerabilities.State.Should().Be(DashboardSignalState.Available);
            overview.Environment.Vulnerabilities.Value.Should().Be(1, "um CVE distinto em aberto");
            overview.Environment.AffectedAssets.Value.Should().Be(1);
            overview.Environment.Vulnerabilities.ObservedAt.Should().NotBeNull();

            overview.Environment.Identity.State.Should().Be(DashboardSignalState.Partial,
                "coleta parcial de identidade é evidência — não é integração sem dados");
            overview.Identity.CapabilitiesCollected.Should().NotBeEmpty();
            overview.Identity.CapabilitiesMissing.Should().ContainSingle()
                .Which.Outcome.Should().Be(nameof(KnightCapabilityOutcome.InsufficientPermission),
                    "a permissão ausente é preservada com o motivo real");

            // (b) …e a dimensão de NEGÓCIO está honestamente vazia, com valores NULOS — nunca zeros.
            overview.BusinessRisk.MaturityState.Should().Be(DashboardSignalState.NeverCollected);
            overview.BusinessRisk.OverallMaturity.Should().BeNull();
            overview.BusinessRisk.IcrState.Should().Be(DashboardSignalState.NeverCollected);
            overview.BusinessRisk.IcrScore.Should().BeNull();
            overview.BusinessRisk.RiskRegisterState.Should().Be(DashboardSignalState.NeverCollected);
            overview.BusinessRisk.CriticalProcessesExposed.Should().BeNull();
            overview.BusinessRisk.OverdueActionPlans.Should().BeNull();

            // (c) As fontes do tenant, com a idade de cada leitura apurada pelo servidor.
            overview.Sources.Items.Should().HaveCount(3, "Secure Score, scanner de vulnerabilidade e identidade");
            overview.Sources.Items.Should().OnlyContain(i => i.EverSynced);

            // (d) A ação do CONTROLLER devolve exatamente esta leitura (mesma composição, mesmas dependências).
            var controller = new DashboardController(
                db, new SystemTenantContext(tenant), new MaturityScoringService(), new IcrScoringService(),
                RealQuery(db, tenant));
            var action = await controller.Overview(CancellationToken.None);
            var body = action.Result.Should().BeOfType<OkObjectResult>().Which.Value
                .Should().BeOfType<DashboardOverviewDto>().Which;
            body.Environment.Assets.Value.Should().Be(2);
            body.BusinessRisk.MaturityState.Should().Be(DashboardSignalState.NeverCollected);
        }

        // ================= ISOLAMENTO: o tenant B não enxerga NADA do tenant A =================
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(other)))
        {
            var overview = await RealQuery(db, other).GetAsync();

            overview.ClientName.Should().Be("Outro Cliente");
            overview.Environment.Assets.Value.Should().Be(1, "apenas o próprio ativo — jamais os 2 do tenant A");
            // [AEGIS-LANGUAGE-STATES-01] O tenant B não tem conector algum: o que está provado é a AUSÊNCIA de
            // fonte, não uma coleta pendente. Antes os dois casos chegavam iguais (NeverCollected).
            overview.Environment.ConfigurationExposures.State.Should().Be(DashboardSignalState.NoSource);
            overview.Environment.ConfigurationExposures.Value.Should().BeNull("sem coleta o valor é nulo, nunca 0");
            overview.Environment.Vulnerabilities.State.Should().Be(DashboardSignalState.NoSource);
            overview.Environment.Vulnerabilities.Value.Should().BeNull();
            overview.Environment.Identity.State.Should().Be(DashboardSignalState.NoSource);
            overview.Sources.Items.Should().BeEmpty("os conectores do tenant A não podem aparecer aqui");
            overview.ConfigurationExposures.Top.Should().BeEmpty();
            overview.Vulnerabilities.Top.Should().BeEmpty();
        }

        _output.WriteLine(
            "✔ Leitura composta da Visão geral executada em PostgreSQL 18 real com dependências reais: " +
            "ambiente observado íntegro sem maturidade legada, risco de negócio nulo (nunca zero) e " +
            "isolamento por tenant confirmado.");
    }

    /// <summary>
    /// Inventário VAZIO não prova ausência de coleta. Com a fonte de inventário JÁ sincronizada e nenhum ativo,
    /// a leitura não pode afirmar "nunca coletado" (contradiz o conector) nem "0 ativos" (inventaria sucesso que
    /// o modelo não registra): o estado é <see cref="DashboardSignalState.Undetermined"/>. Executado em
    /// PostgreSQL real porque a distinção nasce de dois agregados <c>COUNT</c> e da projeção de conectores.
    /// </summary>
    [Fact]
    public async Task InventarioVazio_ComFonteJaSincronizada_NaoAfirmaAusenciaDeColeta_NemZeroAtivos()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.Database.MigrateAsync();
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Inventário Vazio", Slug = "iv-" + tenant.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }

        // (a) Sem ativo E sem fonte de inventário: não há o que coletar — NoSource.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var assets = (await RealQuery(db, tenant).GetAsync()).Environment.Assets;
            assets.State.Should().Be(DashboardSignalState.NoSource);
            assets.Value.Should().BeNull();
            assets.Note.Should().Contain("manual", "o caminho que resta é o cadastro manual");
        }

        // (b) Fonte de inventário conectada e NUNCA sincronizada: aí sim "nunca coletado" está provado.
        Guid scanner;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var c = NewConnector(tenant, ConnectorCapability.VulnerabilityScanner, "Scanner", lastSyncAt: null);
            db.Connectors.Add(c);
            await db.SaveChangesAsync();
            scanner = c.Id;
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var assets = (await RealQuery(db, tenant).GetAsync()).Environment.Assets;
            assets.State.Should().Be(DashboardSignalState.NeverCollected);
            assets.Value.Should().BeNull();
        }

        // (c) A MESMA fonte já sincronizada, e ainda nenhum ativo: o modelo não registra o resultado de uma
        //     coleta de inventário sem achados — a tela declara isso em vez de escolher uma das duas mentiras.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var c = await db.Connectors.SingleAsync(x => x.Id == scanner);
            c.LastSyncAt = Now.AddHours(-2);
            c.LastStatus = ConnectorStatus.Healthy;
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var assets = (await RealQuery(db, tenant).GetAsync()).Environment.Assets;
            assets.State.Should().Be(DashboardSignalState.Undetermined,
                "sincronização concluída sem ativo NÃO prova nem 'nunca coletado' nem 'zero ativos'");
            assets.Value.Should().BeNull("um número aqui seria uma afirmação sem prova");
            assets.Note.Should().NotBeNullOrWhiteSpace();
        }

        // (d) Ativo DESATIVADO é inventário conhecido: zero passa a ser leitura REAL, com valor 0.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            db.Assets.Add(new Asset { TenantId = tenant, Name = "Servidor desativado", Category = AssetCategory.Hardware, Criticality = 1, IsActive = false });
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var assets = (await RealQuery(db, tenant).GetAsync()).Environment.Assets;
            assets.State.Should().Be(DashboardSignalState.Available);
            assets.Value.Should().Be(0, "há inventário; 'nenhum ativo ATIVO' é uma leitura, não uma ausência");
            assets.Note.Should().Contain("1 registro", "a tela explica de onde vem o zero");
        }

        _output.WriteLine(
            "✔ As quatro situações de inventário (sem fonte / nunca sincronizada / sincronizada sem achados / " +
            "inventário conhecido sem ativo ativo) são distinguidas em PostgreSQL real.");
    }

    /// <summary>
    /// Contrato de EXPOSIÇÃO da rota, verificável sem harness HTTP: a ação está sob
    /// <c>GET api/v1/dashboard/overview</c> e NÃO é anônima — permanece coberta pela FallbackPolicy autenticada
    /// declarada no <c>Program.cs</c>. Isto NÃO substitui um teste de pipeline HTTP autenticado, que exigiria
    /// infraestrutura de teste nova.
    /// </summary>
    [Fact]
    public void RotaDoOverview_ContinuaSobAAutenticacaoGlobal()
    {
        var action = typeof(DashboardController).GetMethod(nameof(DashboardController.Overview))!;

        typeof(DashboardController).GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("api/v1/dashboard");
        action.GetCustomAttribute<HttpGetAttribute>()!.Template.Should().Be("overview");

        action.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull(
            "a leitura da tela inicial carrega dados do tenant — sair da FallbackPolicy a tornaria anônima");
        typeof(DashboardController).GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
    }

    // ---- infraestrutura do teste ------------------------------------------------------------------

    /// <summary>
    /// A composição REAL: nenhuma autoridade de leitura é dublada. O único dublê é o registro de coletores de
    /// identidade, que LANÇA se for chamado — a tela inicial nunca aciona coleta externa, e Microsoft/Google
    /// jamais são tocados por este teste.
    /// </summary>
    private static DashboardOverviewQuery RealQuery(AegisScoreDbContext db, Guid tenantId)
    {
        var ctx = new SystemTenantContext(tenantId);
        return new DashboardOverviewQuery(
            db, ctx,
            new WorkspacePostureQuery(db, ctx),
            new PostureExposureQuery(db, ctx, StaticExposureLanguageCatalog.Empty),
            new VulnerabilityQuery(db, ctx),
            new IdentityEvidenceService(db, new KnightCollectorRegistry(new[] { new ThrowingCollector() }), new FixedConfig(), new AegisScore.Infrastructure.Identity.IdentityAcquisitionStore(db, ctx, TimeProvider.System), ctx),
            new MaturityScoringService(),
            new IcrScoringService(),
            new FixedClock(Now));
    }

    private static ConnectorConfig NewConnector(
        Guid tenant, ConnectorCapability capability, string name, DateTimeOffset? lastSyncAt) => new()
    {
        TenantId = tenant,
        Provider = ConnectorProvider.Microsoft,
        Capability = capability,
        DisplayName = name,
        AuthType = ConnectorAuthType.OAuthClientCredentials,
        Enabled = true,
        EncryptedSettings = "{\"clientSecret\":\"s\"}",
        LastSyncAt = lastSyncAt,
        LastStatus = lastSyncAt is null ? ConnectorStatus.Unknown : ConnectorStatus.Healthy,
    };

    private static async Task SeedOperationalTenantAsync(DbContextOptions<AegisScoreDbContext> opt, Guid tenant)
    {
        Guid assetId, scannerId, identityConnectorId;

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var secureScore = NewConnector(tenant, ConnectorCapability.SecureScore, "Microsoft Secure Score", Now.AddHours(-4));
            var scanner = NewConnector(tenant, ConnectorCapability.VulnerabilityScanner, "Defender Vulnerability Management", Now.AddHours(-6));
            var identity = NewConnector(tenant, ConnectorCapability.IdentityPosture, "Microsoft Entra ID · AEGIS KNIGHT", Now.AddHours(-2));
            db.Connectors.AddRange(secureScore, scanner, identity);

            var active1 = new Asset { TenantId = tenant, Name = "Servidor de arquivos", Category = AssetCategory.Hardware, Criticality = 3, IsActive = true };
            var active2 = new Asset { TenantId = tenant, Name = "Estação da diretoria", Category = AssetCategory.Hardware, Criticality = 2, IsActive = true };
            var retired = new Asset { TenantId = tenant, Name = "Notebook devolvido", Category = AssetCategory.Hardware, Criticality = 1, IsActive = false };
            db.Assets.AddRange(active1, active2, retired);
            await db.SaveChangesAsync();

            assetId = active1.Id;
            scannerId = scanner.Id;
            identityConnectorId = identity.Id;

            // Duas exposições ABERTAS + uma resolvida (o cartão conta só as abertas).
            db.PostureExposureFindings.AddRange(
                NewExposure(tenant, secureScore.Id, "mfa-admins", "Exigir MFA para contas administrativas", "Identity", PostureExposureState.Open),
                NewExposure(tenant, secureScore.Id, "legacy-auth", "Bloquear autenticação legada", "Identity", PostureExposureState.Open),
                NewExposure(tenant, secureScore.Id, "audit-log", "Manter log de auditoria ativo", "Apps", PostureExposureState.Resolved));
            await db.SaveChangesAsync();
        }

        // CVE global (TenantId nulo) + exposição do ativo, observada em aberto pelo scanner.
        Guid threatId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            var threat = new Threat
            {
                TenantId = null, Source = ThreatSource.Cve, Code = "CVE-2026-" + Guid.NewGuid().ToString("N")[..6],
                Title = "Execução remota em componente de rede", Severity = "High",
            };
            db.Threats.Add(threat);
            await db.SaveChangesAsync();
            threatId = threat.Id;
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var exposure = new AssetThreatExposure
            {
                TenantId = tenant, AssetId = assetId, ThreatId = threatId,
                Status = ExposureStatus.Active, DetectedAt = Now.AddHours(-6),
            };
            db.AssetThreatExposures.Add(exposure);
            await db.SaveChangesAsync();

            db.AssetThreatObservations.Add(new AssetThreatObservation
            {
                TenantId = tenant, AssetThreatExposureId = exposure.Id,
                ConnectorConfigId = scannerId, LifecycleState = ObservationLifecycle.Open,
            });
            await db.SaveChangesAsync();
        }

        // Snapshot de identidade PARCIAL, gravado pela Evidence Fabric real (uma capacidade sem permissão).
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var ctx = new SystemTenantContext(tenant);
            var registry = new KnightCollectorRegistry(new[] { new FixedCollector(PartialCollection()) });
            await new IdentityEvidenceService(db, registry, new FixedConfig(), new AegisScore.Infrastructure.Identity.IdentityAcquisitionStore(db, ctx, TimeProvider.System), ctx).CollectAsync();
        }

        await using (var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await assert.IdentityEvidenceSnapshots.CountAsync())
                .Should().Be(1, "a coleta de semente precisa ter persistido a evidência que a tela vai LER");
            (await assert.Connectors.CountAsync(c => c.Id == identityConnectorId)).Should().Be(1);
        }
    }

    private static PostureExposureFinding NewExposure(
        Guid tenant, Guid connector, string externalId, string title, string category, PostureExposureState state) => new()
    {
        TenantId = tenant, ConnectorConfigId = connector, ExternalId = externalId, Title = title,
        Category = category, Service = "Microsoft 365", ActionType = "Config",
        CurrentScore = 0, MaxScore = 10, Gap = 10, SourceRank = 1, Tier = "Core",
        LifecycleState = state, FirstSeenAt = Now.AddDays(-10), LastSeenAt = Now.AddHours(-4),
        ResolvedAt = state == PostureExposureState.Resolved ? Now.AddHours(-4) : null,
    };

    private static KnightCollectionResult PartialCollection() => new(
        KnightSourceType.MicrosoftEntraId,
        KnightSourceState.PartialCollection,
        "Microsoft Entra ID",
        new KnightFactSet(new[] { KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, 5) }),
        new[]
        {
            new KnightCapabilityStatus(KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Collected),
            new KnightCapabilityStatus(KnightCapability.IdentityRiskyUsers, KnightCapabilityOutcome.InsufficientPermission,
                "Permissão adicional ainda não concedida no tenant."),
        },
        Now.AddHours(-2),
        "coleta parcial");

    private sealed class FixedCollector : IKnightCollector
    {
        private readonly KnightCollectionResult _result;
        public FixedCollector(KnightCollectionResult result) => _result = result;
        public KnightSourceType Source => KnightSourceType.MicrosoftEntraId;
        public Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default) =>
            Task.FromResult(_result);
    }

    /// <summary>Coletor usado na LEITURA: a tela inicial nunca coleta, então qualquer chamada é um defeito.</summary>
    private sealed class ThrowingCollector : IKnightCollector
    {
        public KnightSourceType Source => KnightSourceType.MicrosoftEntraId;
        public Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default) =>
            throw new InvalidOperationException("Abrir a Visão geral NÃO pode acionar coleta externa.");
    }

    private sealed class FixedConfig : IKnightSourceConfigurationProvider
    {
        public Task<KnightSourceConfiguration> ResolveAsync(Guid tenantId, KnightSourceType source, CancellationToken ct = default) =>
            Task.FromResult<KnightSourceConfiguration>(new KnightEntraIdConfiguration("tenant", "client", "secret"));
        public Task<IReadOnlyList<KnightSourceAvailability>> ListAvailabilityAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnightSourceAvailability>>(Array.Empty<KnightSourceAvailability>());
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
