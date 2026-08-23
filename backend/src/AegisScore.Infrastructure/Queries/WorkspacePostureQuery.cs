using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Queries;
using AegisScore.Application.Scoring;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Queries;

/// <summary>
/// [AEGIS-AUD-021/027/032] Autoridade ÚNICA de leitura do workspace sobre o AegisScoreDbContext: consolida a
/// postura geral e por Função NIST + a saúde dos conectores, TUDO pela fórmula oficial
/// <see cref="AegisScoreFormulaV1"/> (aegis-score-v1), restrita ao catálogo do framework ATIVO. O
/// Dashboard e as seis Funções consomem esta projeção — nenhuma tela recalcula os mesmos conceitos.
///
/// Fail-closed: sem tenant ambiente, devolve VAZIO (o catálogo é global e não pode vazar sozinho). Os estados
/// e conectores são recortados pelo Global Query Filter; não há <c>.Where(TenantId)</c> nem IgnoreQueryFilters.
/// </summary>
public sealed class WorkspacePostureQuery : IWorkspacePostureQuery
{
    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;

    public WorkspacePostureQuery(AegisScoreDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task<WorkspacePostureDto> GetAsync(CancellationToken ct = default)
    {
        // Fail-closed: sem tenant ambiente, nada é projetado.
        if (_tenant.TenantId is null)
            return Empty;

        // Universo ELEGÍVEL: catálogo do framework ATIVO (reference data global), com a Função de cada subcategoria.
        var catalog = await (from s in _db.Subcategories.AsNoTracking()
                             join c in _db.Categories on s.CategoryId equals c.Id
                             join f in _db.Functions on c.FunctionId equals f.Id
                             join fv in _db.FrameworkVersions on f.FrameworkVersionId equals fv.Id
                             where fv.IsActive
                             select new CatalogRow(f.Code, f.Name, f.Order, s.Id, s.MaxScorePoints))
                            .ToListAsync(ct);

        // Estados AVALIADOS do tenant (Global Query Filter fail-closed).
        var states = (await _db.TenantControlStates.AsNoTracking()
                .Select(t => new StateRow(t.SubcategoryId, t.CurrentScore, t.Status, t.LastEvaluatedAt))
                .ToListAsync(ct))
            .ToDictionary(x => x.SubcategoryId);

        // [AEGIS-MVP-ENV-01] Natureza da prova por subcategoria ATIVA — autoridade EXCLUSIVA do tipo persistido
        // em AegisAssessmentRule.EvidenceType (nunca texto/nome de conector/prefixo de Função). A ausência de
        // regra é um estado próprio (NotAutomated), resolvido adiante pela AUSÊNCIA de chave neste mapa.
        var evidenceBySubcategory = await BuildEvidenceMapAsync(catalog, ct);

        // Uma passada em memória (dados pequenos: catálogo é reference data): agrega por Função e no total.
        var byFunction = new Dictionary<string, FunctionAccumulator>(StringComparer.Ordinal);
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var overall = new FunctionAccumulator();
        // Buckets de cobertura por natureza — PARTICIONAM o mesmo universo elegível do total.
        var telemetry = new CoverageAccumulator();
        var documentation = new CoverageAccumulator();
        var both = new CoverageAccumulator();
        var notAutomated = new CoverageAccumulator();
        DateTimeOffset? latestEvidence = null;

        foreach (var row in catalog)
        {
            if (!byFunction.TryGetValue(row.FunctionCode, out var acc))
            {
                acc = new FunctionAccumulator();
                byFunction[row.FunctionCode] = acc;
                order[row.FunctionCode] = row.FunctionOrder;
                names[row.FunctionCode] = row.FunctionName;
            }

            acc.AddEligible(row.MaxScorePoints);
            overall.AddEligible(row.MaxScorePoints);

            // Bucket EXCLUSIVO desta subcategoria: pelo EvidenceType da regra, ou NotAutomated se não houver regra.
            var bucket = evidenceBySubcategory.TryGetValue(row.SubcategoryId, out var et)
                ? et switch
                {
                    RuleEvidenceType.Telemetry => telemetry,
                    RuleEvidenceType.Documentation => documentation,
                    RuleEvidenceType.Both => both,
                    // Inalcançável: BuildEvidenceMapAsync já rejeitou enums inválidos (nunca fallback silencioso).
                    _ => throw new InvalidOperationException(
                        $"EvidenceType não mapeado ({(int)et}) para a subcategoria {row.SubcategoryId}."),
                }
                : notAutomated;
            bucket.AddEligible(row.MaxScorePoints);

            if (states.TryGetValue(row.SubcategoryId, out var st))
            {
                acc.AddEvaluated(row.MaxScorePoints, st.CurrentScore, st.Status);
                overall.AddEvaluated(row.MaxScorePoints, st.CurrentScore, st.Status);
                bucket.AddEvaluated(row.MaxScorePoints);
                if (latestEvidence is null || st.LastEvaluatedAt > latestEvidence)
                    latestEvidence = st.LastEvaluatedAt;
            }
        }

        var functions = byFunction
            .OrderBy(kv => order[kv.Key])
            .Select(kv => ToFunctionDto(kv.Key, names[kv.Key], kv.Value))
            .ToList();

        var evidenceCoverage = new EvidenceCoverageSummaryDto(
            telemetry.ToDto(), documentation.ToDto(), both.ToDto(), notAutomated.ToDto());

        return new WorkspacePostureDto(
            ToOverallDto(overall, latestEvidence), functions, await LoadConnectorsAsync(ct), evidenceCoverage);
    }

    /// <summary>
    /// [AEGIS-MVP-ENV-01] Mapa SubcategoryId → natureza da prova, restrito às subcategorias do catálogo ATIVO
    /// (uma regra de outro framework não perturba esta projeção). A autoridade é EXCLUSIVAMENTE
    /// <c>AegisAssessmentRule.EvidenceType</c>. Falha ACIONÁVEL — nunca fallback silencioso para Telemetry —
    /// diante de um enum inválido (fora de Telemetry/Documentation/Both) ou de duplicidade INCOERENTE (duas
    /// regras para a MESMA subcategoria com naturezas divergentes). A ausência de regra NÃO entra no mapa:
    /// vira NotAutomated no consumidor.
    /// </summary>
    private async Task<Dictionary<Guid, RuleEvidenceType>> BuildEvidenceMapAsync(
        IReadOnlyList<CatalogRow> catalog, CancellationToken ct)
    {
        var catalogIds = catalog.Select(c => c.SubcategoryId).ToHashSet();

        // Reference data GLOBAL (sem filtro de tenant). Só os dois campos escalares — evita materializar jsonb.
        var rules = await _db.AssessmentRules.AsNoTracking()
            .Select(r => new RuleRow(r.SubcategoryId, r.EvidenceType))
            .ToListAsync(ct);

        var map = new Dictionary<Guid, RuleEvidenceType>();
        foreach (var r in rules)
        {
            if (!catalogIds.Contains(r.SubcategoryId))
                continue;   // regra de subcategoria fora do framework ativo — irrelevante para esta projeção.

            if (!Enum.IsDefined(r.EvidenceType))
                throw new InvalidOperationException(
                    $"AegisAssessmentRule com EvidenceType inválido ({(int)r.EvidenceType}) na subcategoria " +
                    $"{r.SubcategoryId}. A classificação de cobertura exige um tipo tipado válido; corrija a regra.");

            if (map.TryGetValue(r.SubcategoryId, out var existing) && existing != r.EvidenceType)
                throw new InvalidOperationException(
                    $"Duplicidade incoerente de regra para a subcategoria {r.SubcategoryId}: naturezas " +
                    $"divergentes ({existing} × {r.EvidenceType}). Deve haver UMA natureza por controle.");

            map[r.SubcategoryId] = r.EvidenceType;
        }

        return map;
    }

    private static WorkspaceOverallDto ToOverallDto(FunctionAccumulator a, DateTimeOffset? latestEvidence)
    {
        var r = a.ToResult();
        // Distribuição por severidade-proxy dos AVALIADOS (mesma régua de SeverityLevels.FromStatus):
        // NonCompliant = Critical, Mitigated = Medium, Compliant = Low. NotEvaluated não é severidade (é lacuna).
        var severities = new List<SeverityCountDto>
        {
            new(nameof(SeverityLevel.Critical), a.NonCompliant),
            new(nameof(SeverityLevel.Medium), a.Mitigated),
            new(nameof(SeverityLevel.Low), a.Compliant),
        };
        return new WorkspaceOverallDto(
            r.FormulaVersion, r.EvaluationState.ToString(), r.Percentage, r.CoveragePercentage,
            r.AchievedScore, r.EvaluatedMaxScore, r.EligibleMaxScore, r.EligibleControls, r.EvaluatedControls,
            a.Compliant, a.NonCompliant, a.Mitigated, r.NotEvaluatedControls, severities, latestEvidence);
    }

    private static FunctionPostureDto ToFunctionDto(string code, string name, FunctionAccumulator a)
    {
        var r = a.ToResult();
        return new FunctionPostureDto(
            code, name, r.EvaluationState.ToString(), r.Percentage, r.CoveragePercentage,
            r.EligibleControls, r.EvaluatedControls, a.Compliant, a.NonCompliant, a.Mitigated, r.NotEvaluatedControls);
    }

    private async Task<ConnectorHealthSummaryDto> LoadConnectorsAsync(CancellationToken ct)
    {
        var items = await _db.Connectors.AsNoTracking()
            .OrderBy(c => c.DisplayName)
            .Select(c => new ConnectorHealthItemDto(
                c.Id, c.DisplayName, c.Provider.ToString(), c.Capability.ToString(),
                c.LastStatus.ToString(), c.LastSyncAt, c.LastSyncAt != null, c.Enabled))
            .ToListAsync(ct);

        // Saúde OPERACIONAL só sobre os HABILITADOS. O desabilitado não entra no denominador (não é falha).
        var enabled = items.Where(i => i.Enabled).ToList();
        // Nunca sincronizado é NeverSynced — jamais Healthy, mesmo com LastStatus incoerente. Entre os que
        // sincronizaram: Failed, Healthy, e todo o resto (Degraded/Unknown) como Degraded — as quatro
        // categorias PARTICIONAM os habilitados (Healthy+Degraded+Failed+NeverSynced == Enabled).
        var neverSynced = enabled.Count(i => !i.EverSynced);
        var healthy = enabled.Count(i => i.EverSynced && i.Status == nameof(ConnectorStatus.Healthy));
        var failed = enabled.Count(i => i.EverSynced && i.Status == nameof(ConnectorStatus.Failed));
        var degraded = enabled.Count(i => i.EverSynced
            && i.Status != nameof(ConnectorStatus.Healthy) && i.Status != nameof(ConnectorStatus.Failed));
        var lastSync = enabled.Max(i => i.LastSyncAt);   // nullable Max: null se nenhum habilitado sincronizou

        return new ConnectorHealthSummaryDto(
            Configured: items.Count, Enabled: enabled.Count, Disabled: items.Count - enabled.Count,
            Healthy: healthy, Degraded: degraded, Failed: failed, NeverSynced: neverSynced,
            LastSyncAt: lastSync, Items: items);
    }

    /// <summary>Fatia de cobertura zerada (sem tenant / bucket vazio): cobertura 0, jamais divisão por zero.</summary>
    private static readonly EvidenceCoverageSliceDto EmptySlice = new(0, 0, 0, 0, 0);

    /// <summary>Projeção vazia (sem tenant): NotEvaluated, sem Funções, conectores nem cobertura — nunca 0% por ausência.</summary>
    private static readonly WorkspacePostureDto Empty = new(
        new WorkspaceOverallDto(
            AegisScoreFormulaV1.Version, nameof(ScoreEvaluationState.NotEvaluated), null, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<SeverityCountDto>(), null),
        Array.Empty<FunctionPostureDto>(),
        new ConnectorHealthSummaryDto(0, 0, 0, 0, 0, 0, 0, null, Array.Empty<ConnectorHealthItemDto>()),
        new EvidenceCoverageSummaryDto(EmptySlice, EmptySlice, EmptySlice, EmptySlice));

    private sealed record CatalogRow(string FunctionCode, string FunctionName, int FunctionOrder, Guid SubcategoryId, int MaxScorePoints);
    private sealed record StateRow(Guid SubcategoryId, int CurrentScore, ControlStatus Status, DateTimeOffset LastEvaluatedAt);
    private sealed record RuleRow(Guid SubcategoryId, RuleEvidenceType EvidenceType);

    /// <summary>Acumulador de agregados de uma Função (ou do total), consolidado pela fórmula única no fim.</summary>
    private sealed class FunctionAccumulator
    {
        public int EligibleControls { get; private set; }
        public int EligibleMax { get; private set; }
        public int EvaluatedControls { get; private set; }
        public int EvaluatedMax { get; private set; }
        public int Achieved { get; private set; }
        public int Compliant { get; private set; }
        public int NonCompliant { get; private set; }
        public int Mitigated { get; private set; }

        public void AddEligible(int maxScorePoints)
        {
            EligibleControls++;
            EligibleMax += maxScorePoints;
        }

        public void AddEvaluated(int maxScorePoints, int currentScore, ControlStatus status)
        {
            EvaluatedControls++;
            EvaluatedMax += maxScorePoints;
            Achieved += currentScore;
            switch (status)
            {
                case ControlStatus.Compliant: Compliant++; break;
                case ControlStatus.NonCompliant: NonCompliant++; break;
                case ControlStatus.MitigatedByThirdParty: Mitigated++; break;
            }
        }

        public AegisScoreResult ToResult() =>
            AegisScoreFormulaV1.Compute(Achieved, EvaluatedMax, EvaluatedControls, EligibleMax, EligibleControls);
    }

    /// <summary>
    /// [AEGIS-MVP-ENV-01] Acumulador de UMA fatia de cobertura por natureza de prova. Só cobertura ponderada
    /// (elegível × avaliado por <c>MaxScorePoints</c>) — sem numerador de score. A cobertura reusa o MESMO
    /// arredondamento oficial da fórmula e trata bucket sem peso elegível como 0 (nunca divisão por zero).
    /// </summary>
    private sealed class CoverageAccumulator
    {
        private int _eligibleControls;
        private int _evaluatedControls;
        private int _eligibleMax;
        private int _evaluatedMax;

        public void AddEligible(int maxScorePoints)
        {
            _eligibleControls++;
            _eligibleMax += maxScorePoints;
        }

        public void AddEvaluated(int maxScorePoints)
        {
            _evaluatedControls++;
            _evaluatedMax += maxScorePoints;
        }

        public EvidenceCoverageSliceDto ToDto() => new(
            _eligibleControls, _evaluatedControls, _eligibleMax, _evaluatedMax,
            _eligibleMax > 0 ? AegisScoreFormulaV1.RoundPercentage((double)_evaluatedMax / _eligibleMax * 100) : 0);
    }
}
