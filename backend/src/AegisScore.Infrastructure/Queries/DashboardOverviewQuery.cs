using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Identity;
using AegisScore.Application.Knight;
using AegisScore.Application.Queries;
using AegisScore.Application.Scoring;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Queries;

/// <summary>
/// [AEGIS-MVP-PRODUCT-01] Leitura COMPOSTA da tela inicial — PURA COMPOSIÇÃO das autoridades já existentes
/// (<see cref="IWorkspacePostureQuery"/>, <see cref="IPostureExposureQuery"/>, <see cref="IVulnerabilityQuery"/>,
/// <see cref="IIdentityEvidenceService"/>) mais contagens AGREGADAS no banco. Nunca recalcula score, gap,
/// cobertura, CVSS, lifecycle ou postura; nunca combina dimensões num índice único; nunca aciona coleta.
///
/// Custo controlado por construção: o inventário de ativos e o registro de riscos entram por <c>COUNT</c>
/// (e um <c>GROUP BY</c> mínimo para a última avaliação de cada risco) — nenhuma lista é materializada para
/// ser contada em memória. As filas de exposição e vulnerabilidade pedem a PÁGINA 1 com teto de
/// <see cref="DashboardOverviewDto.MaxQueueItems"/> itens.
///
/// As queries são SCOPED e compartilham o mesmo <c>AegisScoreDbContext</c> por requisição — por isso as
/// chamadas são SEQUENCIAIS (um DbContext não é thread-safe; nada de <c>Task.WhenAll</c> aqui). O tenant é
/// IMPLÍCITO: herdado por construção do <see cref="ITenantContext"/> + Global Query Filter fail-closed.
/// </summary>
public sealed class DashboardOverviewQuery : IDashboardOverviewQuery
{
    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IWorkspacePostureQuery _posture;
    private readonly IPostureExposureQuery _exposures;
    private readonly IVulnerabilityQuery _vulnerabilities;
    private readonly IIdentityEvidenceService _identity;
    private readonly MaturityScoringService _maturity;
    private readonly IcrScoringService _icr;
    private readonly TimeProvider _clock;

    public DashboardOverviewQuery(
        AegisScoreDbContext db,
        ITenantContext tenant,
        IWorkspacePostureQuery posture,
        IPostureExposureQuery exposures,
        IVulnerabilityQuery vulnerabilities,
        IIdentityEvidenceService identity,
        MaturityScoringService maturity,
        IcrScoringService icr,
        TimeProvider clock)
    {
        _db = db;
        _tenant = tenant;
        _posture = posture;
        _exposures = exposures;
        _vulnerabilities = vulnerabilities;
        _identity = identity;
        _maturity = maturity;
        _icr = icr;
        _clock = clock;
    }

    public async Task<DashboardOverviewDto> GetAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        var tenant = _tenant.TenantId is Guid id
            ? await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct)
            : null;

        // ---- Postura determinística (autoridade única; a MESMA do /scoring/workspace) ----
        var workspace = await _posture.GetAsync(ct);

        // ---- Filas curtas: página 1, somente abertos, ordenação preservada VERBATIM ----
        var exposures = await _exposures.GetAsync(
            new PostureExposureFilter(
                State: PostureExposureStateFilter.Open,
                Page: 1,
                PageSize: DashboardOverviewDto.MaxQueueItems),
            ct);

        var vulnerabilities = await _vulnerabilities.GetOverviewAsync(
            new VulnerabilityFilter(
                State: VulnerabilityLifecycleFilter.Open,
                Page: 1,
                PageSize: DashboardOverviewDto.MaxQueueItems),
            ct);

        // ---- Identidade: ÚLTIMO snapshot da Evidence Fabric (sem nova aquisição, sem Graph) ----
        var identity = await _identity.GetLatestProjectionAsync(ct);

        var environment = await BuildEnvironmentAsync(exposures.Summary, vulnerabilities.Summary, identity, ct);
        var businessRisk = await BuildBusinessRiskAsync(ct);

        return new DashboardOverviewDto(
            ReadModelVersion: DashboardOverviewDto.Version,
            GeneratedAt: now,
            ClientName: tenant?.Name ?? "—",
            Posture: workspace.Overall,
            EvidenceCoverage: workspace.EvidenceCoverage,
            Environment: environment,
            BusinessRisk: businessRisk,
            ConfigurationExposures: new PriorityExposureQueueDto(exposures.Summary, exposures.Items),
            Vulnerabilities: new PriorityVulnerabilityQueueDto(vulnerabilities.Summary, vulnerabilities.Groups),
            Identity: BuildIdentity(identity),
            Sources: BuildSources(workspace.Connectors, now));
    }

    // ---------------------------------------------------------------------------------------------
    // Ambiente observado
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Métricas do que JÁ foi observado. O inventário de ativos entra por <c>COUNT</c>; exposições e
    /// vulnerabilidades reaproveitam os resumos que as próprias autoridades acabaram de produzir (nenhuma
    /// segunda consulta). Ausência de coleta vira valor NULO — jamais zero.
    /// </summary>
    private async Task<DashboardEnvironmentDto> BuildEnvironmentAsync(
        PostureExposureSummaryDto exposures,
        VulnerabilitySummaryDto vulnerabilities,
        IdentityEvidenceProjection identity,
        CancellationToken ct)
    {
        // Ativos: COUNT no banco — o inventário NUNCA é materializado para ser contado. Nasce de descoberta
        // contínua/seed, e "nenhum ativo" é indistinguível de "nunca coletado": zero vira NeverCollected.
        //
        // ⚠️ Sem instante de observação aqui de propósito. O agregado sobre a data do ativo (MAX/ORDER BY em
        // DateTimeOffset) NÃO é suportado pelo provider SQLite da suíte — a mesma limitação já registrada no
        // AEGIS_STATE §22.7 —, e resolvê-la carregando as datas contradiria a regra de não materializar o
        // inventário. A recência das fontes vive, com autoridade, no bloco de saúde das fontes.
        var activeAssets = await _db.Assets.AsNoTracking().CountAsync(a => a.IsActive, ct);

        var assets = activeAssets > 0
            ? new DashboardMetricDto(DashboardSignalState.Available, activeAssets, "Inventário de ativos")
            : new DashboardMetricDto(
                DashboardSignalState.NeverCollected, null, "Inventário de ativos", null,
                "Nenhum ativo descoberto ainda — o inventário aparece após a primeira coleta do ambiente.");

        // Exposições de configuração: o resumo já distingue "nunca coletado" por LastCollectedAt nulo.
        var exposuresCollected = exposures.LastCollectedAt is not null;
        var configurationExposures = exposuresCollected
            ? new DashboardMetricDto(
                DashboardSignalState.Available, exposures.TotalOpen, exposures.SourceLabel, exposures.LastCollectedAt)
            : new DashboardMetricDto(
                DashboardSignalState.NeverCollected, null, exposures.SourceLabel, null,
                "Ainda não coletado — nenhuma leitura de configuração foi feita neste ambiente.");

        // Vulnerabilidades: NeverCollected é um campo EXPLÍCITO do resumo — respeitado sem reinterpretação.
        var vulnerabilitySource = vulnerabilities.Sources.Count > 0
            ? string.Join(" · ", vulnerabilities.Sources.Select(s => s.Provider).Distinct())
            : "Gestão de vulnerabilidades";

        var vulnerabilityMetric = vulnerabilities.NeverCollected
            ? new DashboardMetricDto(
                DashboardSignalState.NeverCollected, null, vulnerabilitySource, null,
                "Ainda não coletado — nenhuma varredura de vulnerabilidades chegou a este ambiente.")
            : new DashboardMetricDto(
                DashboardSignalState.Available, vulnerabilities.DistinctCvesOpen, vulnerabilitySource,
                vulnerabilities.LastCollectedAt);

        var affectedAssets = vulnerabilities.NeverCollected
            ? new DashboardMetricDto(DashboardSignalState.NeverCollected, null, vulnerabilitySource)
            : new DashboardMetricDto(
                DashboardSignalState.Available, vulnerabilities.AffectedAssetsOpen, vulnerabilitySource,
                vulnerabilities.LastCollectedAt);

        // Identidade: a contagem exibida é de capacidades ENTREGUES, não de usuários (o snapshot é agregado
        // e sem PII). O estado vem do estado de coleta da Evidence Fabric, preservado sem reinterpretação.
        var identityCollected = identity.Capabilities.Count(c => c.Outcome == KnightCapabilityOutcome.Collected);
        var identityMetric = IdentityStateOf(identity) switch
        {
            DashboardSignalState.Available => new DashboardMetricDto(
                DashboardSignalState.Available, identityCollected, identity.Source, identity.CollectedAt),
            DashboardSignalState.Partial => new DashboardMetricDto(
                DashboardSignalState.Partial, identityCollected, identity.Source, identity.CollectedAt,
                "Coleta parcial — parte das capacidades de identidade não foi entregue pela fonte."),
            DashboardSignalState.NeverCollected => new DashboardMetricDto(
                DashboardSignalState.NeverCollected, null, identity.Source, null,
                "Conector de identidade configurado, porém nenhuma coleta produziu evidência ainda."),
            _ => new DashboardMetricDto(
                DashboardSignalState.NoSource, null, identity.Source, null,
                "Nenhuma fonte de identidade conectada neste ambiente."),
        };

        return new DashboardEnvironmentDto(
            assets, configurationExposures, vulnerabilityMetric, affectedAssets, identityMetric);
    }

    // ---------------------------------------------------------------------------------------------
    // Risco de negócio (maturidade / registro de riscos / ICR)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Dimensão de risco de NEGÓCIO — deliberadamente separada da postura por controle. Cada sub-dimensão
    /// tem estado próprio: pode existir registro de riscos sem maturidade avaliada, e vice-versa. Sem
    /// avaliação, os valores são NULOS e a tela diz "não avaliado" — nunca zeros que leem como "sem risco".
    /// </summary>
    private async Task<DashboardBusinessRiskDto> BuildBusinessRiskAsync(CancellationToken ct)
    {
        // ---- Maturidade: só materializa as avaliações se houver ao menos UMA (COUNT primeiro) ----
        var evaluationRows = await (from s in _db.Scopes
                                    join e in _db.Evaluations on s.Id equals e.AssessmentScopeId
                                    join sub in _db.Subcategories on e.SubcategoryId equals sub.Id
                                    select new { sub.Code, e.CurrentScore, e.TargetScore })
                                   .AsNoTracking().ToListAsync(ct);

        var maturityState = evaluationRows.Count > 0
            ? DashboardSignalState.Available
            : DashboardSignalState.NeverCollected;

        double? overallMaturity = null;
        double? targetMaturity = null;
        if (evaluationRows.Count > 0)
        {
            var rollup = _maturity.Aggregate(
                evaluationRows.Select(x => new SubcategoryScore(x.Code, x.CurrentScore, x.TargetScore)));
            overallMaturity = rollup.Overall.CurrentScore;
            targetMaturity = rollup.Overall.TargetScore;
        }

        // ---- Registro de riscos: a ÚLTIMA avaliação de cada risco ----
        // ⚠️ O "group by … select g.OrderByDescending(…).First()" NÃO traduz (nem no SQLite da suíte nem no
        // PostgreSQL): a subconsulta correlacionada quebra na tradução — a mesma armadilha registrada no
        // AEGIS_STATE §22.7. Por isso a projeção sai em TIPO ANÔNIMO com apenas as quatro colunas necessárias
        // (nada de entidades) e o "último por risco" é resolvido em memória. Um COUNT antecede a
        // materialização: num tenant sem registro de riscos nenhuma linha é carregada.
        var hasRiskEvaluations = await _db.RiskEvaluations.AsNoTracking().AnyAsync(ct);

        var riskLatest = hasRiskEvaluations
            ? (await (from ev in _db.RiskEvaluations.AsNoTracking()
                      join r in _db.Risks.AsNoTracking() on ev.RiskId equals r.Id
                      select new { ev.RiskId, ev.RiskLevel, ev.EvaluatedAt, r.BusinessProcessId })
                     .ToListAsync(ct))
                .GroupBy(x => x.RiskId)
                .Select(g => g.OrderByDescending(x => x.EvaluatedAt).First())
                .ToList()
            : [];

        var riskRegisterState = riskLatest.Count > 0
            ? DashboardSignalState.Available
            : DashboardSignalState.NeverCollected;

        long? criticalProcessesExposed = riskLatest.Count > 0
            ? riskLatest
                .Where(x => x.RiskLevel is RiskLevel.Alto or RiskLevel.Critico)
                .Select(x => x.BusinessProcessId)
                .Where(pid => pid != null)
                .Distinct()
                .Count()
            : null;

        // Planos de ação: IsOverdue é uma propriedade calculada do domínio (não traduz para SQL). O universo
        // é o dos planos ligados a risco — pequeno por natureza e materializado só quando existe registro.
        long? overdueActionPlans = null;
        if (riskLatest.Count > 0)
        {
            var actionPlans = await (from r in _db.Risks
                                     join ap in _db.ActionPlans on r.Id equals ap.RiskId
                                     select ap).AsNoTracking().ToListAsync(ct);
            overdueActionPlans = actionPlans.Count(ap => ap.IsOverdue);
        }

        // ---- ICR: EXCLUSIVAMENTE os valores persistidos; nunca fabricado a partir de constantes ----
        var storedIcr = await _db.IcrScores.AsNoTracking().Select(s => s.Score).ToListAsync(ct);
        double? icrScore = null;
        string? icrBand = null;
        if (storedIcr.Count > 0)
        {
            icrScore = Math.Round(storedIcr.Average(), 1);
            icrBand = _icr.BandOf(icrScore.Value).ToString();
        }

        return new DashboardBusinessRiskDto(
            MaturityState: maturityState,
            OverallMaturity: overallMaturity,
            TargetMaturity: targetMaturity,
            EvaluatedSubcategories: evaluationRows.Count,
            IcrState: storedIcr.Count > 0 ? DashboardSignalState.Available : DashboardSignalState.NeverCollected,
            IcrScore: icrScore,
            IcrBand: icrBand,
            RiskRegisterState: riskRegisterState,
            RisksEvaluated: riskLatest.Count,
            CriticalProcessesExposed: criticalProcessesExposed,
            OverdueActionPlans: overdueActionPlans);
    }

    // ---------------------------------------------------------------------------------------------
    // Identidade e fontes
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Traduz o estado de coleta da Evidence Fabric para o estado de painel, PRESERVANDO a distinção entre
    /// "sem fonte", "nunca coletou" e "coletou parcialmente". Uma coleta parcial (permissão ausente) é
    /// <see cref="DashboardSignalState.Partial"/> — a integração continua entregando o que pode.
    /// </summary>
    private static DashboardSignalState IdentityStateOf(IdentityEvidenceProjection p) => p.CollectionState switch
    {
        IdentityEvidenceCollectionState.Complete => p.IsDegraded ? DashboardSignalState.Partial : DashboardSignalState.Available,
        IdentityEvidenceCollectionState.Partial => DashboardSignalState.Partial,
        IdentityEvidenceCollectionState.NeverCollected => DashboardSignalState.NeverCollected,
        _ => DashboardSignalState.NoSource,
    };

    private static DashboardIdentityDto BuildIdentity(IdentityEvidenceProjection p) => new(
        State: IdentityStateOf(p),
        CollectionState: p.CollectionState,
        SourceLabel: p.Source,
        CollectedAt: p.CollectedAt,
        IsDegraded: p.IsDegraded,
        CapabilitiesCollected: p.Capabilities
            .Where(c => c.Outcome == KnightCapabilityOutcome.Collected)
            .Select(c => c.Capability.ToString())
            .ToList(),
        CapabilitiesMissing: p.Capabilities
            .Where(c => c.Outcome != KnightCapabilityOutcome.Collected)
            .Select(c => new DashboardIdentityGapDto(c.Capability.ToString(), c.Outcome.ToString(), c.Detail))
            .ToList(),
        ControlsAwaitingEvidence: p.Controls
            .Count(c => c.State == IdentityControlEvidenceState.CollectedButInsufficient));

    /// <summary>
    /// Reprojeta a saúde de conectores que a projeção do workspace já apurou, acrescentando os dias desde a
    /// última sincronização e ordenando por GRAVIDADE — o que precisa de atenção primeiro. Os contadores são
    /// copiados VERBATIM: esta composição não redefine o que é "saudável".
    /// </summary>
    private static DashboardSourcesDto BuildSources(ConnectorHealthSummaryDto c, DateTimeOffset now)
    {
        var items = c.Items
            .Select(i => new DashboardSourceDto(
                i.Id.ToString(), i.DisplayName, i.Provider, i.Capability, i.Status,
                i.Enabled, i.EverSynced, i.LastSyncAt,
                i.LastSyncAt is { } sync ? (int)Math.Floor((now - sync).TotalDays) : null))
            // Habilitados primeiro; entre eles, o que nunca sincronizou e o mais antigo antes do resto.
            .OrderByDescending(i => i.Enabled)
            .ThenBy(i => i.EverSynced)
            .ThenBy(i => i.LastSyncAt ?? DateTimeOffset.MinValue)
            .ThenBy(i => i.DisplayName)
            .ToList();

        return new DashboardSourcesDto(
            Configured: c.Configured,
            Enabled: c.Enabled,
            Disabled: c.Disabled,
            Healthy: c.Healthy,
            Degraded: c.Degraded,
            Failed: c.Failed,
            NeverSynced: c.NeverSynced,
            // Fontes a verificar: habilitadas que não estão saudáveis, nunca sincronizaram OU estão antigas.
            // É contagem de APRESENTAÇÃO — não altera score nem estado de conector.
            Attention: c.Degraded + c.Failed + c.NeverSynced
                + items.Count(i => i.Enabled && i.EverSynced
                    && string.Equals(i.Status, "Healthy", StringComparison.OrdinalIgnoreCase)
                    && i.StaleDays >= DashboardOverviewDto.StaleAfterDays),
            LastSyncAt: c.LastSyncAt,
            Items: items);
    }
}
