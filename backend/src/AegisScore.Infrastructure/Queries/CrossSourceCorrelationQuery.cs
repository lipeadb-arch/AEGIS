using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AegisScore.Application.Queries;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AssetRow = AegisScore.Infrastructure.Queries.CrossSourceAssetRow;

namespace AegisScore.Infrastructure.Queries;

/// <summary>
/// [AEGIS-CROSS-SOURCE-01] Obtenção dos fatos e da proveniência para as situações entre fontes, e projeção nos contratos
/// de leitura. A decisão é SEMPRE de <see cref="CrossSourceCorrelationEvaluator"/> (autoridade única); aqui não há
/// regra em SQL — o banco só filtra por tenant, agrega observações e pagina CVEs.
///
/// Leitura COERENTE: todas as consultas de uma requisição correm numa única transação somente leitura, em
/// <c>REPEATABLE READ</c> no PostgreSQL (uma fotografia do banco para todos os SELECTs). Uma ingestão que publica em
/// lotes pode estar no meio do caminho — a fotografia lida é consistente (marca, desfecho, registros e observações
/// do mesmo instante), e cada fato é qualificado pela marca da aquisição que o observou: nada é combinado em silêncio.
///
/// Limites: nenhuma observação do tenant é carregada inteira — as observações são AGREGADAS no banco por
/// (ativo, conector, ciclo de vida, marca); as CVEs são paginadas no banco, por ativo. A população avaliada pela
/// Central tem teto (<see cref="MaxEvaluatedAssets"/>), declarado no resumo quando atingido.
/// </summary>
public sealed class CrossSourceCorrelationQuery : ICrossSourceCorrelationQuery
{
    internal const int MaxEvaluatedAssets = 5_000;
    internal const int MaxCvePageSize = 50;
    internal const int MaxListPageSize = 50;
    internal const int CvePreviewSize = 3;

    /// <summary>Ponto de observação para testes de intercalação: depois de ler as fontes, antes de ler os fatos.</summary>
    internal const string CheckpointFactsPending = "facts-pending";

    private readonly AegisScoreDbContext _db;
    private readonly TimeProvider _clock;
    private readonly CrossSourcePolicy _policy;

    /// <summary>SOMENTE para testes: barreira chamada em <see cref="CheckpointFactsPending"/>, dentro da leitura.</summary>
    internal Func<string, CancellationToken, Task>? Checkpoint { get; init; }

    public CrossSourceCorrelationQuery(
        AegisScoreDbContext db, TimeProvider clock, IOptions<CrossSourceCorrelationOptions> options)
    {
        _db = db;
        _clock = clock;
        _policy = options.Value.ToPolicy();
    }

    /// <summary>Chave de UMA CVE por ativo — classe com propriedades para o EF ordenar/deduplicar depois do UNION.</summary>
    private sealed class CveKey
    {
        public Guid ThreatId { get; set; }
        public string Code { get; set; } = "";
    }

    // ---- Detalhe do ativo -------------------------------------------------------------------------------------------

    public async Task<AssetCrossSourceDto?> GetForAssetAsync(
        Guid assetId, int cvePage, int cvePageSize, CancellationToken ct = default)
    {
        var page = Math.Max(1, cvePage);
        var size = Math.Clamp(cvePageSize, 1, MaxCvePageSize);
        var now = _clock.GetUtcNow();

        return await InReadSnapshotAsync(async () =>
        {
            var asset = await _db.Assets.AsNoTracking()
                .Where(a => a.Id == assetId)
                .Select(a => new AssetRow(a.Id, a.Name, a.NameOrigin))
                .FirstOrDefaultAsync(ct);
            if (asset is null) return null;

            var connectors = await LoadConnectorsAsync(ct);
            await HitAsync(CheckpointFactsPending, ct);
            var facts = (await LoadFactsAsync(new[] { asset }, connectors, ct))[asset.Id];
            var assessment = CrossSourceCorrelationEvaluator.Evaluate(facts, _policy, now);

            CrossSourceCvePageDto? cves = null;
            int? openCves = null;
            if (assessment.Vulnerabilities.Count > 0)
            {
                var outOfPolicy = assessment.Vulnerabilities.Sum(v => v.OpenOutOfPolicy);
                var noLonger = assessment.Vulnerabilities.Sum(v => v.NoLongerReported);
                var keys = EligibleCveKeys(asset.Id, assessment.Vulnerabilities);
                if (keys is null)
                {
                    openCves = 0;
                    cves = new CrossSourceCvePageDto(0, page, size, Array.Empty<CrossSourceCveDto>(), outOfPolicy, noLonger);
                }
                else
                {
                    var total = await keys.CountAsync(ct);
                    var pageKeys = await keys
                        .OrderBy(k => k.Code).ThenBy(k => k.ThreatId)
                        .Skip((page - 1) * size).Take(size)
                        .ToListAsync(ct);
                    openCves = total;
                    cves = new CrossSourceCvePageDto(
                        total, page, size, await CveDetailsAsync(asset.Id, pageKeys, assessment, ct), outOfPolicy, noLonger);
                }
            }

            return new AssetCrossSourceDto(
                AssetId: asset.Id,
                AssetName: asset.Name,
                NameIsPlaceholder: asset.NameOrigin == AssetNameOrigin.Placeholder,
                EvaluatedAt: now,
                Heading: CrossSourceNarrative.Heading,
                Scope: CrossSourceNarrative.Scope,
                AssociationCriterion: CrossSourceNarrative.AssociationCriterion,
                Association: CrossSourceAssociationDto.From(assessment.Association),
                Policy: CrossSourcePolicyDto.From(_policy),
                Rules: assessment.Rules.Select(r => ToRuleDto(r, assessment, openCves)).ToList(),
                Evidence: assessment.Records.Select(ToEvidenceDto).ToList(),
                Cves: cves);
        }, ct);
    }

    // ---- Central de Prioridades -------------------------------------------------------------------------------------

    public async Task<CrossSourceSituationListDto> ListAsync(CrossSourceSituationFilter filter, CancellationToken ct = default)
    {
        var state = CrossSourceStates.IsKnown(filter.State) ? filter.State! : CrossSourceStates.Identified;
        var rule = CrossSourceRules.Find(filter.RuleCode);
        var page = Math.Max(1, filter.Page);
        var size = Math.Clamp(filter.PageSize, 1, MaxListPageSize);
        var now = _clock.GetUtcNow();

        return await InReadSnapshotAsync(async () =>
        {
            var connectors = await LoadConnectorsAsync(ct);
            await HitAsync(CheckpointFactsPending, ct);
            var vulnIds = connectors.Values.Where(c => c.Role == CrossSourceRole.Vulnerabilities).Select(c => c.ConnectorId).ToList();
            var mgmtIds = connectors.Values.Where(c => c.Role == CrossSourceRole.DeviceManagement).Select(c => c.ConnectorId).ToList();
            var (reading, note) = Reading(connectors.Values);

            // População avaliável: ativos com registro das DUAS fontes (qualquer estado) — condição de aplicabilidade,
            // não critério da regra. A decisão continua sendo do avaliador.
            var population = _db.Assets.AsNoTracking().Where(a =>
                _db.AssetSourceBindings.Any(b => b.AssetId == a.Id && vulnIds.Contains(b.ConnectorConfigId))
                && _db.AssetSourceBindings.Any(b => b.AssetId == a.Id && mgmtIds.Contains(b.ConnectorConfigId)));
            var populationTotal = vulnIds.Count == 0 || mgmtIds.Count == 0 ? 0 : await population.CountAsync(ct);
            var assets = populationTotal == 0
                ? new List<AssetRow>()
                : await population
                    .OrderBy(a => a.Name).ThenBy(a => a.Id)
                    .Take(MaxEvaluatedAssets)
                    .Select(a => new AssetRow(a.Id, a.Name, a.NameOrigin))
                    .ToListAsync(ct);

            var facts = await LoadFactsAsync(assets, connectors, ct);
            var assessments = assets
                .Select(a => CrossSourceCorrelationEvaluator.Evaluate(facts[a.Id], _policy, now))
                .ToList();

            var byRule = CrossSourceRules.All.Select(r =>
            {
                var results = assessments.Select(a => a.Rules.Single(x => x.Rule.Code == r.Code)).ToList();
                return new CrossSourceRuleTallyDto(
                    r.Code, r.Version, r.Title,
                    Identified: results.Count(x => x.State == CrossSourceStates.Identified),
                    IdentifiedWithCaveats: results.Count(x => x.State == CrossSourceStates.Identified && x.Caveats.Count > 0),
                    NotIdentified: results.Count(x => x.State == CrossSourceStates.NotIdentified),
                    NotIdentifiedWithCaveats: results.Count(x => x.State == CrossSourceStates.NotIdentified && x.Caveats.Count > 0),
                    InsufficientEvidence: results.Count(x => x.State == CrossSourceStates.InsufficientEvidence),
                    LinkConflict: results.Count(x => x.State == CrossSourceStates.LinkConflict),
                    ContradictoryEvidence: results.Count(x => x.State == CrossSourceStates.ContradictoryEvidence));
            }).ToList();

            var matching = assessments
                .SelectMany(a => a.Rules
                    .Where(r => r.State == state && (rule is null || r.Rule.Code == rule.Code))
                    .Select(r => (Asset: a, Rule: r)))
                .OrderBy(x => x.Asset.Facts.AssetName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Asset.Facts.AssetName, StringComparer.Ordinal)
                .ThenBy(x => x.Asset.Facts.AssetId)
                .ThenBy(x => RuleIndex(x.Rule.Rule.Code))
                .ToList();
            var pageItems = matching.Skip((page - 1) * size).Take(size).ToList();

            // CVEs só para os ativos da PÁGINA (contagem e prévia paginadas no banco, uma vez por ativo).
            var cveByAsset = new Dictionary<Guid, (int Total, List<string> Preview)>();
            foreach (var a in pageItems.Select(x => x.Asset).DistinctBy(a => a.Facts.AssetId))
            {
                var keys = EligibleCveKeys(a.Facts.AssetId, a.Vulnerabilities);
                if (keys is null)
                {
                    if (a.Vulnerabilities.Count > 0) cveByAsset[a.Facts.AssetId] = (0, new List<string>());
                    continue;
                }
                var total = await keys.CountAsync(ct);
                var preview = await keys
                    .OrderBy(k => k.Code).ThenBy(k => k.ThreatId)
                    .Take(CvePreviewSize)
                    .Select(k => k.Code)
                    .ToListAsync(ct);
                cveByAsset[a.Facts.AssetId] = (total, preview);
            }

            var items = pageItems.Select(x =>
            {
                var cves = cveByAsset.TryGetValue(x.Asset.Facts.AssetId, out var c) ? c : ((int Total, List<string> Preview)?)null;
                // Datas: as aquisições que o AVALIADOR declarou participantes — intervalo e quantidade, nunca só a máxima.
                return new CrossSourceSituationItemDto(
                    AssetId: x.Asset.Facts.AssetId,
                    AssetName: x.Asset.Facts.AssetName,
                    NameIsPlaceholder: x.Asset.Facts.NameIsPlaceholder,
                    RuleCode: x.Rule.Rule.Code,
                    RuleVersion: x.Rule.Rule.Version,
                    Title: x.Rule.Rule.Title,
                    State: x.Rule.State,
                    StateLabel: CrossSourceNarrative.StateLabel(x.Rule.State, x.Rule.Caveats.Count > 0),
                    HasCaveats: x.Rule.Caveats.Count > 0,
                    Summary: CrossSourceNarrative.Summary(x.Rule, cves?.Total),
                    OpenCveCount: cves?.Total,
                    CvePreview: cves?.Preview ?? new List<string>(),
                    CvePreviewTruncated: cves is { } p && p.Total > p.Preview.Count,
                    Caveats: x.Rule.Caveats,
                    EvidenceBasis: x.Rule.EvidenceBasis,
                    VulnerabilityAcquisitions: CrossSourceAcquisitionSpanDto.From(x.Rule.VulnerabilityAcquisitions),
                    DeviceManagementAcquisitions: CrossSourceAcquisitionSpanDto.From(x.Rule.DeviceAcquisitions));
            }).ToList();

            var summary = new CrossSourceSituationSummaryDto(
                ReadingState: reading,
                ReadingNote: note,
                AssetsWithBothSources: populationTotal,
                AssetsEvaluated: assessments.Count,
                EvaluationTruncated: populationTotal > assessments.Count,
                SituationsIdentified: byRule.Sum(r => r.Identified),
                AssetsWithSituations: assessments.Count(a => a.Rules.Any(r => r.State == CrossSourceStates.Identified)),
                ByRule: byRule);

            return new CrossSourceSituationListDto(
                EvaluatedAt: now,
                Heading: CrossSourceNarrative.Heading,
                Scope: CrossSourceNarrative.Scope,
                Policy: CrossSourcePolicyDto.From(_policy),
                Summary: summary,
                StateFilter: state,
                RuleFilter: rule?.Code,
                Items: items,
                Total: matching.Count,
                Page: page,
                PageSize: size);
        }, ct);
    }

    // ---- Fatos ------------------------------------------------------------------------------------------------------

    // A aquisição dos fatos é COMPARTILHADA com a prioridade de tratamento (CrossSourceFactReader): as duas superfícies
    // leem registros, marcas, desfechos e observações do mesmo jeito.
    private Task<Dictionary<Guid, CrossSourceConnectorFacts>> LoadConnectorsAsync(CancellationToken ct) =>
        CrossSourceFactReader.LoadConnectorsAsync(_db, ct);

    private Task<Dictionary<Guid, CrossSourceAssetFacts>> LoadFactsAsync(
        IReadOnlyList<AssetRow> assets, IReadOnlyDictionary<Guid, CrossSourceConnectorFacts> connectors, CancellationToken ct) =>
        CrossSourceFactReader.LoadFactsAsync(_db, assets, connectors, ct);

    // ---- CVEs -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// CVEs DISTINTAS em aberto do ativo, de cada conector usável e SÓ nas marcas dentro da política (UNION deduplica a
    /// mesma CVE reportada por mais de uma fonte). <c>null</c> quando nenhum conector tem marca elegível.
    /// </summary>
    private IQueryable<CveKey>? EligibleCveKeys(Guid assetId, IReadOnlyList<CrossSourceVulnerabilityEvidence> evidence)
    {
        IQueryable<CveKey>? query = null;
        foreach (var e in evidence.Where(x => x.EligibleMarkers.Count > 0))
        {
            var connectorId = e.Connector.ConnectorId;
            var markers = e.EligibleMarkers.ToList();
            var part =
                from o in _db.AssetThreatObservations.AsNoTracking()
                join x in _db.AssetThreatExposures.AsNoTracking() on o.AssetThreatExposureId equals x.Id
                join t in _db.Threats.AsNoTracking() on x.ThreatId equals t.Id
                where x.AssetId == assetId
                    && o.ConnectorConfigId == connectorId
                    && o.LifecycleState == ObservationLifecycle.Open
                    && markers.Contains(o.LastSeenAt)
                select new CveKey { ThreatId = t.Id, Code = t.Code };
            query = query is null ? part : query.Union(part);
        }
        return query;
    }

    /// <summary>Detalhe e proveniência (por fonte) SÓ das CVEs da página.</summary>
    private async Task<IReadOnlyList<CrossSourceCveDto>> CveDetailsAsync(
        Guid assetId, IReadOnlyList<CveKey> pageKeys, CrossSourceAssetAssessment assessment, CancellationToken ct)
    {
        if (pageKeys.Count == 0) return Array.Empty<CrossSourceCveDto>();
        var threatIds = pageKeys.Select(k => k.ThreatId).ToList();
        var markersByConnector = assessment.Vulnerabilities.ToDictionary(
            v => v.Connector.ConnectorId, v => v.EligibleMarkers.ToHashSet());
        var connectorIds = markersByConnector.Keys.ToList();

        var threats = await _db.Threats.AsNoTracking()
            .Where(t => threatIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Code, t.Title, t.Severity, t.CvssScore })
            .ToDictionaryAsync(t => t.Id, ct);
        var provenance = (await (
                    from o in _db.AssetThreatObservations.AsNoTracking()
                    join x in _db.AssetThreatExposures.AsNoTracking() on o.AssetThreatExposureId equals x.Id
                    where x.AssetId == assetId
                        && threatIds.Contains(x.ThreatId)
                        && connectorIds.Contains(o.ConnectorConfigId)
                        && o.LifecycleState == ObservationLifecycle.Open
                    select new { x.ThreatId, o.ConnectorConfigId, o.FirstSeenAt, o.LastSeenAt })
                .ToListAsync(ct))
            // Exatidão por conector: só as marcas elegíveis DAQUELE conector sustentam a CVE.
            .Where(p => markersByConnector[p.ConnectorConfigId].Contains(p.LastSeenAt))
            .ToLookup(p => p.ThreatId);
        var connectors = assessment.Vulnerabilities.ToDictionary(v => v.Connector.ConnectorId, v => v.Connector);

        return pageKeys.Select(k =>
        {
            var t = threats[k.ThreatId];
            return new CrossSourceCveDto(
                t.Code,
                string.IsNullOrWhiteSpace(t.Title) || string.Equals(t.Title, t.Code, StringComparison.OrdinalIgnoreCase)
                    ? null : t.Title,
                t.Severity,
                t.CvssScore,
                provenance[k.ThreatId]
                    .OrderBy(p => connectors[p.ConnectorConfigId].ConnectorName, StringComparer.Ordinal)
                    .ThenBy(p => p.ConnectorConfigId)
                    .Select(p =>
                    {
                        var c = connectors[p.ConnectorConfigId];
                        var state = CrossSourceCorrelationEvaluator.AcquisitionState(p.LastSeenAt, c);
                        return new CrossSourceCveSourceDto(
                            c.Label, c.ConnectorName, p.FirstSeenAt, p.LastSeenAt, state, CrossSourceNarrative.AcquisitionLabel(state));
                    })
                    .ToList());
        }).ToList();
    }

    // ---- Projeção ---------------------------------------------------------------------------------------------------

    private static CrossSourceRuleResultDto ToRuleDto(CrossSourceRuleAssessment r, CrossSourceAssetAssessment a, int? openCves) =>
        new(
            RuleCode: r.Rule.Code,
            RuleVersion: r.Rule.Version,
            Title: r.Rule.Title,
            State: r.State,
            StateLabel: CrossSourceNarrative.StateLabel(r.State, r.Caveats.Count > 0),
            HasCaveats: r.Caveats.Count > 0,
            Summary: CrossSourceNarrative.Summary(r, openCves),
            SupportingData: CrossSourceNarrative.SupportingData(r, a, openCves),
            WhatToVerify: CrossSourceNarrative.WhatToVerify(r),
            Limitation: r.Rule.Limitation,
            Criteria: r.Rule.Criteria,
            Caveats: r.Caveats,
            Reasons: r.Reasons,
            OpenCveCount: a.Vulnerabilities.Count > 0 ? openCves : null);

    private static CrossSourceEvidenceRecordDto ToEvidenceDto(CrossSourceRecordAssessment r)
    {
        var b = r.Binding;
        var (resolution, _) = AssetCrossSourceNarrative.ForBinding(b.ResolutionState, b.ConflictKind, DirectoryIdentifierStatus.NotEvaluated);
        var management = r.Connector.Role == CrossSourceRole.DeviceManagement;
        return new CrossSourceEvidenceRecordDto(
            Source: string.IsNullOrWhiteSpace(b.SourceLabel) ? r.Connector.Label : b.SourceLabel!,
            ConnectorName: r.Connector.ConnectorName,
            Role: r.Connector.Role == CrossSourceRole.Vulnerabilities ? "vulnerabilities" : "deviceManagement",
            RoleLabel: CrossSourceNarrative.RoleLabel(r.Connector.Role),
            Eligible: r.Eligible,
            Eligibility: r.Eligibility,
            EligibilityLabel: CrossSourceNarrative.EligibilityLabel(r.Eligibility),
            ExclusionReason: r.ExclusionReason,
            IsActive: b.IsActive,
            FirstObservedAt: b.FirstObservedAt,
            AcquiredAt: b.LastObservedAt,
            SourceActivityAt: b.SourceLastSeenAt,
            NoLongerObservedSince: b.IsActive ? null : b.ResolvedAt,
            LinkedAt: b.LinkedAt,
            AcquisitionState: r.AcquisitionState,
            AcquisitionLabel: CrossSourceNarrative.AcquisitionLabel(r.AcquisitionState),
            ResolutionLabel: resolution,
            ComplianceLabel: management ? AssetCrossSourceNarrative.ComplianceLabel(b.Compliance) ?? "Conformidade não informada" : null,
            EncryptionLabel: management ? AssetCrossSourceNarrative.EncryptionLabel(b.Encryption) ?? "Criptografia não informada" : null,
            LatestAttemptFailed: r.Connector.LatestAttemptFailed);
    }

    private static (string State, string? Note) Reading(IEnumerable<CrossSourceConnectorFacts> connectors)
    {
        var list = connectors.ToList();
        var vuln = list.Where(c => c.Role == CrossSourceRole.Vulnerabilities).ToList();
        var mgmt = list.Where(c => c.Role == CrossSourceRole.DeviceManagement).ToList();
        if (vuln.Count == 0 || mgmt.Count == 0)
            return (CrossSourceReadingStates.NoSource,
                "As regras exigem as duas fontes: " +
                (vuln.Count == 0 ? "nenhuma integração do Microsoft Defender (vulnerabilidades) " : "") +
                (vuln.Count == 0 && mgmt.Count == 0 ? "e " : "") +
                (mgmt.Count == 0 ? "nenhuma integração do Microsoft Intune (dispositivos) " : "") +
                "está configurada neste ambiente.");
        if (vuln.All(c => c.Watermark is null) || mgmt.All(c => c.Watermark is null))
            return (CrossSourceReadingStates.NeverCollected,
                "As duas fontes estão configuradas, mas ao menos uma ainda não publicou uma leitura por dispositivo.");
        return (CrossSourceReadingStates.Available, null);
    }

    private Task HitAsync(string checkpoint, CancellationToken ct) =>
        Checkpoint is null ? Task.CompletedTask : Checkpoint(checkpoint, ct);

    private static int RuleIndex(string code)
    {
        for (var i = 0; i < CrossSourceRules.All.Count; i++)
            if (CrossSourceRules.All[i].Code == code) return i;
        return int.MaxValue;
    }

    /// <summary>
    /// Executa a leitura numa ÚNICA transação somente leitura — <c>REPEATABLE READ</c> no PostgreSQL, para que todos os
    /// SELECTs vejam a mesma fotografia do banco. Se já houver transação no contexto, participa dela.
    /// </summary>
    private Task<T> InReadSnapshotAsync<T>(Func<Task<T>> read, CancellationToken ct) =>
        CrossSourceFactReader.InReadSnapshotAsync(_db, read, ct);
}
