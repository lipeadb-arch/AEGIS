using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AegisScore.Application.Queries;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Queries;

/// <summary>
/// [AEGIS-RISK-PRIORITIZATION-01] Aquisição dos fatos e projeção da prioridade de tratamento em dispositivos. A decisão é
/// SEMPRE de <see cref="DevicePriorityEvaluator"/> (que usa a autoridade de correlação para atribuição, política temporal,
/// associação e situações); o banco só filtra por tenant, agrega casos pela classificação da CVE e pagina.
///
/// Seleção de candidatos ANTES da paginação (nunca "os primeiros N em ordem alfabética"): para cada dispositivo com
/// vulnerabilidade em aberto, o banco calcula um LIMITE SUPERIOR da faixa — a melhor faixa base entre TODAS as observações
/// em aberto (superconjunto das elegíveis) antecipada de uma posição se o dispositivo PODERIA ter agravante (criticidade
/// declarada alta ou registro do Intune). A faixa real nunca é melhor que esse limite. Os dispositivos são avaliados em
/// ordem desse limite; se a população passa do teto, os não avaliados têm limite ≥ o do último avaliado, então as faixas
/// anteriores a ele estão COMPLETAS — e o resumo diz exatamente quais.
///
/// Leitura COERENTE: a mesma transação REPEATABLE READ da correlação (uma fotografia para todos os SELECTs). Limites: as
/// observações nunca são carregadas por CVE — são agregadas por (ativo, conector, marca, classificação); só as CVEs da
/// página (e a determinante de cada dispositivo da página) são lidas.
/// </summary>
public sealed class DevicePriorityQuery : IDevicePriorityQuery
{
    internal const int DefaultMaxEvaluatedAssets = 5_000;
    internal const int MaxCasePageSize = 50;
    internal const int MaxListPageSize = 50;
    private const int Chunk = 500;

    /// <summary>Ponto de observação para testes de intercalação: depois de ler as fontes, antes de ler os fatos.</summary>
    internal const string CheckpointFactsPending = "facts-pending";

    /// <summary>Faixa-limite dos dispositivos cujas CVEs em aberto não têm severidade técnica (não priorizáveis).</summary>
    private const int NoBand = 9;

    private readonly AegisScoreDbContext _db;
    private readonly TimeProvider _clock;
    private readonly CrossSourcePolicy _policy;

    /// <summary>SOMENTE para testes: barreira chamada em <see cref="CheckpointFactsPending"/>, dentro da leitura.</summary>
    internal Func<string, CancellationToken, Task>? Checkpoint { get; init; }

    /// <summary>Teto de dispositivos avaliados por leitura (testes reduzem para provar o recorte declarado).</summary>
    internal int MaxEvaluatedAssets { get; init; } = DefaultMaxEvaluatedAssets;

    public DevicePriorityQuery(AegisScoreDbContext db, TimeProvider clock, IOptions<CrossSourceCorrelationOptions> options)
    {
        _db = db;
        _clock = clock;
        _policy = options.Value.ToPolicy();
    }

    private sealed record AssetInfo(
        Guid Id, string Name, AssetNameOrigin NameOrigin, int Criticality,
        int? DeclaredValue, DateTimeOffset? DeclaredAt, string? DeclaredByName, string? Note, int Potential)
    {
        public CrossSourceAssetRow Row => new(Id, Name, NameOrigin);
        public DevicePriorityCriticalityFacts CriticalityFacts => new(Criticality, DeclaredValue, DeclaredAt, DeclaredByName, Note);
    }

    /// <summary>Chave de UM caso para ordenar/paginar no banco — classe com propriedades (o EF ordena depois do UNION).</summary>
    private sealed class CaseKey
    {
        public Guid ExposureId { get; set; }
        public Guid ThreatId { get; set; }
        public string Code { get; set; } = "";
        public int SeverityOrder { get; set; }
        public int ExploitOrder { get; set; }
        public double? Cvss { get; set; }
    }

    private IQueryable<DevicePriorityThreatRank> Ranks => _db.Threats.AsNoTracking().Select(DevicePriorityPolicy.Rank);

    // ---- Central de Prioridades -------------------------------------------------------------------------------------

    public async Task<DevicePriorityListDto> ListAsync(DevicePriorityFilter filter, CancellationToken ct = default)
    {
        var band = DevicePriorityBands.IsKnownFilter(filter.Band) ? filter.Band : null;
        var page = Math.Max(1, filter.Page);
        var size = Math.Clamp(filter.PageSize, 1, MaxListPageSize);
        var now = _clock.GetUtcNow();

        return await CrossSourceFactReader.InReadSnapshotAsync(_db, async () =>
        {
            var connectors = await CrossSourceFactReader.LoadConnectorsAsync(_db, ct);
            await HitAsync(CheckpointFactsPending, ct);
            var vulnIds = connectors.Values.Where(c => c.Role == CrossSourceRole.Vulnerabilities).Select(c => c.ConnectorId).ToList();
            var mgmtIds = connectors.Values.Where(c => c.Role == CrossSourceRole.DeviceManagement).Select(c => c.ConnectorId).ToList();
            var outOfScope = await _db.Connectors.AsNoTracking()
                .CountAsync(c => c.Capability == ConnectorCapability.VulnerabilityScanner && c.Provider != ConnectorProvider.Microsoft, ct);
            var (reading, note) = Reading(connectors.Values);

            // ---- Candidatos: limite superior da faixa por dispositivo, calculado no banco -------------------------------
            var open =
                from o in _db.AssetThreatObservations.AsNoTracking()
                join x in _db.AssetThreatExposures.AsNoTracking() on o.AssetThreatExposureId equals x.Id
                join t in Ranks on x.ThreatId equals t.ThreatId
                where vulnIds.Contains(o.ConnectorConfigId)
                    && o.LifecycleState == ObservationLifecycle.Open
                    && x.Status == ExposureStatus.Active
                select new { x.AssetId, t.SeverityOrder, t.ExploitOrder };
            // Faixa base no banco: limitar(severidade + exploit − 1, 1, 4) — forma equivalente da tabela da política
            // (conferida pelos testes); severidade não informada vira NoBand.
            var perAsset = open
                .GroupBy(r => r.AssetId)
                .Select(g => new
                {
                    AssetId = g.Key,
                    BestBase = g.Min(r => r.SeverityOrder >= DevicePrioritySeverity.Unknown ? NoBand
                        : r.SeverityOrder + r.ExploitOrder <= 2 ? 1
                        : r.SeverityOrder + r.ExploitOrder >= 5 ? 4
                        : r.SeverityOrder + r.ExploitOrder - 1),
                    BestRaw = g.Min(r => r.SeverityOrder >= DevicePrioritySeverity.Unknown ? 99 : r.SeverityOrder + r.ExploitOrder),
                });
            var candidates =
                from p in perAsset
                join a in _db.Assets.AsNoTracking() on p.AssetId equals a.Id
                let mayAggravate =
                    (a.CriticalityDeclaredAt != null && a.CriticalityDeclaredValue >= DevicePriorityPolicy.HighCriticality
                        && a.CriticalityDeclaredValue == a.Criticality)
                    || _db.AssetSourceBindings.Any(b => b.AssetId == a.Id && b.IsActive && mgmtIds.Contains(b.ConnectorConfigId))
                select new
                {
                    a.Id, a.Name, a.NameOrigin, a.Criticality,
                    a.CriticalityDeclaredValue, a.CriticalityDeclaredAt, a.CriticalityDeclaredByName, a.CriticalityDeclarationNote,
                    Potential = p.BestBase >= NoBand ? NoBand : mayAggravate && p.BestBase > 1 ? p.BestBase - 1 : p.BestBase,
                    p.BestRaw,
                };

            var population = vulnIds.Count == 0 ? 0 : await candidates.CountAsync(ct);
            var evaluatedRows = population == 0
                ? new List<AssetInfo>()
                : (await candidates
                        .OrderBy(c => c.Potential).ThenBy(c => c.BestRaw).ThenBy(c => c.Id)
                        .Take(MaxEvaluatedAssets)
                        .ToListAsync(ct))
                    .Select(c => new AssetInfo(c.Id, c.Name, c.NameOrigin, c.Criticality, c.CriticalityDeclaredValue,
                        c.CriticalityDeclaredAt, c.CriticalityDeclaredByName, c.CriticalityDeclarationNote, c.Potential))
                    .ToList();

            // ---- Avaliação (autoridade pura) em lotes ---------------------------------------------------------------------
            var assessments = new List<(AssetInfo Asset, DevicePriorityAssessment A)>(evaluatedRows.Count);
            foreach (var chunk in evaluatedRows.Chunk(Chunk))
            {
                var facts = await LoadPriorityFactsAsync(chunk, connectors, vulnIds, ct);
                assessments.AddRange(chunk.Select(a => (a, DevicePriorityEvaluator.Evaluate(facts[a.Id], _policy, now))));
            }

            var prioritized = assessments
                .Where(x => x.A.Status == DevicePriorityStatuses.Prioritized)
                .OrderBy(x => x.A.Best!.Value, Comparer<DevicePriorityKey>.Create(DevicePriorityPolicy.Compare))
                .ThenBy(x => x.Asset.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Asset.Name, StringComparer.Ordinal)
                .ThenBy(x => x.Asset.Id)
                .ToList();
            var insufficient = assessments
                .Where(x => x.A.Status == DevicePriorityStatuses.Insufficient)
                .OrderBy(x => x.Asset.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Asset.Name, StringComparer.Ordinal)
                .ThenBy(x => x.Asset.Id)
                .ToList();
            var ties = prioritized.GroupBy(x => x.A.Best!.Value).ToDictionary(g => g.Key, g => g.Count() - 1);

            var matching = band switch
            {
                null => prioritized,
                DevicePriorityBands.Insufficient => insufficient,
                _ => prioritized.Where(x => x.A.Band == DevicePriorityBands.RankOf(band)).ToList(),
            };
            var offset = (page - 1) * size;
            var pageItems = matching.Skip(offset).Take(size).ToList();

            var items = new List<DevicePriorityItemDto>(pageItems.Count);
            for (var i = 0; i < pageItems.Count; i++)
            {
                var (asset, a) = pageItems[i];
                var determining = a.Best is null ? default : (await CasesAsync(asset.Id, a, 1, 1, ct)).Items.FirstOrDefault();
                items.Add(ToItem(asset, a, determining.Facts, determining.Dto, offset + i + 1,
                    a.Best is { } k ? ties.GetValueOrDefault(k) : 0));
            }

            // ---- Resumo e recorte ---------------------------------------------------------------------------------------
            var truncated = population > evaluatedRows.Count;
            string? completeThrough = null;
            string? truncationNote = null;
            if (truncated)
            {
                var cutoff = evaluatedRows[^1].Potential;
                completeThrough = cutoff >= NoBand ? DevicePriorityBands.P4 : cutoff > 1 ? DevicePriorityBands.Of(cutoff - 1) : null;
                truncationNote =
                    $"Avaliados {evaluatedRows.Count} de {population} dispositivos com vulnerabilidade em aberto (teto por leitura), " +
                    "escolhidos pelo limite superior da faixa calculado dos fatos das CVEs — não por ordem alfabética. " +
                    (completeThrough is null
                        ? "Nenhuma faixa está completa: dispositivos não avaliados ainda podem estar em Prioridade 1."
                        : $"As faixas até {DevicePriorityNarrative.BandLabel(completeThrough).Split(" · ")[0]} estão completas; " +
                          "dispositivos não avaliados só podem estar nas faixas seguintes ou sem prioridade.");
            }

            var assetsByBand = DevicePriorityBands.Prioritized
                .Select(b => new DevicePriorityCountDto(b, DevicePriorityNarrative.BandLabel(b),
                    prioritized.Count(x => x.A.Band == DevicePriorityBands.RankOf(b))))
                .Append(new DevicePriorityCountDto(DevicePriorityBands.Insufficient,
                    DevicePriorityNarrative.BandLabel(DevicePriorityBands.Insufficient), insufficient.Count))
                .ToList();
            var casesByBand = DevicePriorityBands.Prioritized
                .Select(b => new DevicePriorityCountDto(b, DevicePriorityNarrative.BandLabel(b),
                    assessments.Sum(x => x.A.CasesByBand.GetValueOrDefault(DevicePriorityBands.RankOf(b)!.Value))))
                .Append(new DevicePriorityCountDto(DevicePriorityBands.Insufficient,
                    DevicePriorityNarrative.BandLabel(DevicePriorityBands.Insufficient), assessments.Sum(x => x.A.InsufficientCases)))
                .ToList();
            // Disposições humanas em TODO o tenant (não só nos avaliados): casos em aberto na fonte, fora da fila por decisão
            // registrada — a evidência continua no banco e a leitura não a altera.
            var dispositions = Dispositions(vulnIds.Count == 0
                ? Array.Empty<KeyValuePair<ExposureStatus, int>>()
                : (await (
                        from o in _db.AssetThreatObservations.AsNoTracking()
                        join x in _db.AssetThreatExposures.AsNoTracking() on o.AssetThreatExposureId equals x.Id
                        where vulnIds.Contains(o.ConnectorConfigId)
                            && o.LifecycleState == ObservationLifecycle.Open && x.Status != ExposureStatus.Active
                        group x by x.Status into g
                        select new { Status = g.Key, Count = g.Count() })
                    .ToListAsync(ct))
                .Select(r => new KeyValuePair<ExposureStatus, int>(r.Status, r.Count)));

            var summary = new DevicePrioritySummaryDto(
                ReadingState: reading,
                ReadingNote: note,
                CandidateAssets: population,
                AssetsEvaluated: evaluatedRows.Count,
                EvaluationTruncated: truncated,
                CompleteThroughBand: completeThrough,
                TruncationNote: truncationNote,
                AssetsByBand: assetsByBand,
                CasesByBand: casesByBand,
                Dispositions: dispositions,
                OutOfScopeSources: outOfScope,
                OutOfScopeNote: outOfScope == 0 ? null
                    : $"{outOfScope} integração(ões) de vulnerabilidades de outros provedores estão configuradas e não são " +
                      "avaliadas por esta versão da política (a atribuição por dispositivo só existe para o Microsoft Defender).");

            return new DevicePriorityListDto(
                EvaluatedAt: now,
                Heading: DevicePriorityNarrative.Heading,
                Scope: DevicePriorityNarrative.Scope,
                Policy: DevicePriorityNarrative.Policy(_policy),
                Summary: summary,
                BandFilter: band,
                Items: items,
                Total: matching.Count,
                Page: page,
                PageSize: size);
        }, ct);
    }

    // ---- Detalhe do ativo -------------------------------------------------------------------------------------------

    public async Task<AssetDevicePriorityDto?> GetForAssetAsync(
        Guid assetId, int casePage, int casePageSize, CancellationToken ct = default)
    {
        var page = Math.Max(1, casePage);
        var size = Math.Clamp(casePageSize, 1, MaxCasePageSize);
        var now = _clock.GetUtcNow();

        return await CrossSourceFactReader.InReadSnapshotAsync(_db, async () =>
        {
            var asset = await _db.Assets.AsNoTracking()
                .Where(a => a.Id == assetId)
                .Select(a => new AssetInfo(a.Id, a.Name, a.NameOrigin, a.Criticality, a.CriticalityDeclaredValue,
                    a.CriticalityDeclaredAt, a.CriticalityDeclaredByName, a.CriticalityDeclarationNote, 0))
                .FirstOrDefaultAsync(ct);
            if (asset is null) return null;

            var connectors = await CrossSourceFactReader.LoadConnectorsAsync(_db, ct);
            await HitAsync(CheckpointFactsPending, ct);
            var vulnIds = connectors.Values.Where(c => c.Role == CrossSourceRole.Vulnerabilities).Select(c => c.ConnectorId).ToList();
            var a = DevicePriorityEvaluator.Evaluate(
                (await LoadPriorityFactsAsync(new[] { asset }, connectors, vulnIds, ct))[asset.Id], _policy, now);

            var cases = await CasesAsync(asset.Id, a, page, size, ct);
            var determining = a.Best is null ? default
                : page == 1 ? cases.Items.FirstOrDefault()
                : (await CasesAsync(asset.Id, a, 1, 1, ct)).Items.FirstOrDefault();

            return new AssetDevicePriorityDto(
                AssetId: asset.Id,
                AssetName: asset.Name,
                NameIsPlaceholder: asset.NameOrigin == AssetNameOrigin.Placeholder,
                EvaluatedAt: now,
                Heading: DevicePriorityNarrative.Heading,
                Scope: DevicePriorityNarrative.Scope,
                Policy: DevicePriorityNarrative.Policy(_policy),
                Status: a.Status,
                Band: BandOf(a),
                BandLabel: DevicePriorityNarrative.StatusBandLabel(a),
                PositionReason: DevicePriorityNarrative.PositionReason(a, determining.Facts),
                DeterminingCase: determining.Dto,
                InformationLabel: DevicePriorityNarrative.InformationLabel(a, determining.Facts),
                Factors: DevicePriorityNarrative.Factors(a, determining.Facts),
                CouldChange: DevicePriorityNarrative.CouldChange(a),
                Limitations: DevicePriorityNarrative.Limitations,
                Caveats: Caveats(a, determining.Facts),
                NextAction: DevicePriorityNarrative.NextAction(a, determining.Facts),
                Criticality: DevicePriorityNarrative.CriticalityDto(asset.CriticalityFacts),
                Situations: a.Correlation.Rules
                    .Select(r => new DevicePrioritySituationDto(r.Rule.Code, r.Rule.Version, r.Rule.Title, r.State,
                        CrossSourceNarrative.StateLabel(r.State, r.Caveats.Count > 0), r.Caveats.Count > 0))
                    .ToList(),
                CasesByBand: DevicePriorityBands.Prioritized
                    .Select(b => new DevicePriorityCountDto(b, DevicePriorityNarrative.BandLabel(b),
                        a.CasesByBand.GetValueOrDefault(DevicePriorityBands.RankOf(b)!.Value)))
                    .ToList(),
                InsufficientCases: a.InsufficientCases,
                Dispositions: Dispositions(a.Dispositions),
                NoLongerReported: a.NoLongerReported,
                ExcludedOutOfPolicy: a.OpenOutOfPolicy,
                Cases: a.EligibleMarkers.Count == 0 ? null : cases.Page);
        }, ct);
    }

    // ---- Fatos ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Fatos da autoridade de correlação (a MESMA leitura das situações entre fontes) + casos em aberto agregados pela
    /// classificação da CVE e disposições — por (ativo, conector, marca), nunca por CVE.
    /// </summary>
    private async Task<Dictionary<Guid, DevicePriorityFacts>> LoadPriorityFactsAsync(
        IReadOnlyList<AssetInfo> assets, IReadOnlyDictionary<Guid, CrossSourceConnectorFacts> connectors,
        IReadOnlyList<Guid> vulnIds, CancellationToken ct)
    {
        var correlation = await CrossSourceFactReader.LoadFactsAsync(_db, assets.Select(a => a.Row).ToList(), connectors, ct);
        var ids = assets.Select(a => a.Id).ToList();
        var ruleVulnIds = vulnIds.ToList();

        var caseRows = ruleVulnIds.Count == 0 ? new() : await (
                from o in _db.AssetThreatObservations.AsNoTracking()
                join x in _db.AssetThreatExposures.AsNoTracking() on o.AssetThreatExposureId equals x.Id
                join t in Ranks on x.ThreatId equals t.ThreatId
                where ids.Contains(x.AssetId) && ruleVulnIds.Contains(o.ConnectorConfigId)
                    && o.LifecycleState == ObservationLifecycle.Open && x.Status == ExposureStatus.Active
                group o by new { x.AssetId, o.ConnectorConfigId, o.LastSeenAt, t.SeverityOrder, t.ExploitOrder, t.Cvss } into g
                select new
                {
                    g.Key.AssetId, g.Key.ConnectorConfigId, g.Key.LastSeenAt, g.Key.SeverityOrder, g.Key.ExploitOrder, g.Key.Cvss,
                    Count = g.Count(),
                })
            .ToListAsync(ct);
        var dispositionRows = ruleVulnIds.Count == 0 ? new() : await (
                from o in _db.AssetThreatObservations.AsNoTracking()
                join x in _db.AssetThreatExposures.AsNoTracking() on o.AssetThreatExposureId equals x.Id
                where ids.Contains(x.AssetId) && ruleVulnIds.Contains(o.ConnectorConfigId)
                    && o.LifecycleState == ObservationLifecycle.Open && x.Status != ExposureStatus.Active
                group o by new { x.AssetId, o.ConnectorConfigId, o.LastSeenAt, x.Status } into g
                select new { g.Key.AssetId, g.Key.ConnectorConfigId, g.Key.LastSeenAt, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        var casesByAsset = caseRows.ToLookup(r => r.AssetId);
        var dispositionsByAsset = dispositionRows.ToLookup(r => r.AssetId);
        return assets.ToDictionary(a => a.Id, a => new DevicePriorityFacts(
            correlation[a.Id],
            a.CriticalityFacts,
            casesByAsset[a.Id]
                .Select(r => new DevicePriorityCaseGroup(r.ConnectorConfigId, r.LastSeenAt, r.SeverityOrder, r.ExploitOrder, r.Cvss, r.Count))
                .ToList(),
            dispositionsByAsset[a.Id]
                .Select(r => new DevicePriorityDispositionGroup(r.ConnectorConfigId, r.LastSeenAt, r.Status, r.Count))
                .ToList()));
    }

    // ---- Casos ------------------------------------------------------------------------------------------------------

    private sealed record CasePage(
        DevicePriorityCasePageDto Page, IReadOnlyList<(DevicePriorityCaseFacts Facts, DevicePriorityCaseDto Dto)> Items);

    /// <summary>
    /// Casos em aberto (disposição ativa) do dispositivo, SÓ nas marcas elegíveis de cada conector usável, na ordem da
    /// política — ordenados e paginados no banco; detalhe e proveniência só da página.
    /// </summary>
    private async Task<CasePage> CasesAsync(Guid assetId, DevicePriorityAssessment a, int page, int size, CancellationToken ct)
    {
        IQueryable<CaseKey>? keys = null;
        foreach (var (connectorId, markers) in a.EligibleMarkers.OrderBy(m => m.Key))
        {
            var ms = markers.ToList();
            var part =
                from o in _db.AssetThreatObservations.AsNoTracking()
                join x in _db.AssetThreatExposures.AsNoTracking() on o.AssetThreatExposureId equals x.Id
                join t in Ranks on x.ThreatId equals t.ThreatId
                where x.AssetId == assetId
                    && o.ConnectorConfigId == connectorId
                    && o.LifecycleState == ObservationLifecycle.Open
                    && x.Status == ExposureStatus.Active
                    && ms.Contains(o.LastSeenAt)
                select new CaseKey
                {
                    ExposureId = x.Id, ThreatId = t.ThreatId, Code = t.Code,
                    SeverityOrder = t.SeverityOrder, ExploitOrder = t.ExploitOrder, Cvss = t.Cvss,
                };
            keys = keys is null ? part : keys.Union(part);
        }
        if (keys is null)
            return new CasePage(new DevicePriorityCasePageDto(0, page, size, Array.Empty<DevicePriorityCaseDto>()),
                Array.Empty<(DevicePriorityCaseFacts, DevicePriorityCaseDto)>());

        var total = await keys.CountAsync(ct);
        // Ordem da política no banco: faixa (mesma forma equivalente da tabela; sem severidade por último), exploit,
        // severidade, CVSS maior primeiro — o agravante é do dispositivo inteiro e não muda a ordem entre seus casos —,
        // e o identificador da CVE só para estabilidade (empate real).
        var pageKeys = await keys
            .OrderBy(k => k.SeverityOrder >= DevicePrioritySeverity.Unknown ? NoBand
                : k.SeverityOrder + k.ExploitOrder <= 2 ? 1
                : k.SeverityOrder + k.ExploitOrder >= 5 ? 4
                : k.SeverityOrder + k.ExploitOrder - 1)
            .ThenBy(k => k.ExploitOrder)
            .ThenBy(k => k.SeverityOrder)
            .ThenByDescending(k => k.Cvss ?? -1)
            .ThenBy(k => k.Code)
            .ThenBy(k => k.ThreatId)
            .Skip((page - 1) * size).Take(size)
            .ToListAsync(ct);
        if (pageKeys.Count == 0)
            return new CasePage(new DevicePriorityCasePageDto(total, page, size, Array.Empty<DevicePriorityCaseDto>()),
                Array.Empty<(DevicePriorityCaseFacts, DevicePriorityCaseDto)>());

        var threatIds = pageKeys.Select(k => k.ThreatId).ToList();
        var exposureIds = pageKeys.Select(k => k.ExposureId).ToList();
        var connectorIds = a.EligibleMarkers.Keys.ToList();
        var threats = await _db.Threats.AsNoTracking()
            .Where(t => threatIds.Contains(t.Id))
            .Select(t => new
            {
                t.Id, t.Code, t.Title, t.Severity, t.CvssScore, t.CvssVector, t.PublicExploit, t.ExploitVerified, t.Epss,
                t.KnownExploited, t.UpdatedOn,
            })
            .ToDictionaryAsync(t => t.Id, ct);
        var provenance = (await _db.AssetThreatObservations.AsNoTracking()
                .Where(o => exposureIds.Contains(o.AssetThreatExposureId)
                    && connectorIds.Contains(o.ConnectorConfigId)
                    && o.LifecycleState == ObservationLifecycle.Open)
                .Select(o => new { o.AssetThreatExposureId, o.ConnectorConfigId, o.FirstSeenAt, o.LastSeenAt })
                .ToListAsync(ct))
            // Exatidão por conector: só as marcas elegíveis DAQUELE conector sustentam o caso.
            .Where(p => a.EligibleMarkers[p.ConnectorConfigId].Contains(p.LastSeenAt))
            .ToLookup(p => p.AssetThreatExposureId);
        var connectors = a.Source.Evidence.ToDictionary(e => e.Connector.ConnectorId, e => e.Connector);

        var items = new List<(DevicePriorityCaseFacts, DevicePriorityCaseDto)>(pageKeys.Count);
        foreach (var k in pageKeys)
        {
            var t = threats[k.ThreatId];
            var p = provenance[k.ExposureId]
                .OrderByDescending(x => x.LastSeenAt).ThenBy(x => x.ConnectorConfigId).First();
            var facts = new DevicePriorityCaseFacts(
                t.Id, t.Code,
                string.IsNullOrWhiteSpace(t.Title) || string.Equals(t.Title, t.Code, StringComparison.OrdinalIgnoreCase) ? null : t.Title,
                t.Severity, t.CvssScore, t.CvssVector, t.PublicExploit, t.ExploitVerified, t.Epss, t.KnownExploited, t.UpdatedOn,
                p.ConnectorConfigId, p.FirstSeenAt, p.LastSeenAt);
            var connector = connectors[p.ConnectorConfigId];
            var baseBand = DevicePriorityPolicy.BaseBand(k.SeverityOrder, k.ExploitOrder);
            var caseBand = baseBand is { } bb ? DevicePriorityBands.Of(DevicePriorityPolicy.FinalBand(bb, a.Aggravated)) : DevicePriorityBands.Insufficient;
            var acquisition = CrossSourceCorrelationEvaluator.AcquisitionState(p.LastSeenAt, connector);
            items.Add((facts, new DevicePriorityCaseDto(
                CveId: t.Code,
                Title: facts.Title,
                Band: caseBand,
                BandLabel: DevicePriorityNarrative.BandLabel(caseBand),
                Reason: DevicePriorityNarrative.CaseReason(k.SeverityOrder, k.ExploitOrder, k.Cvss, a.Aggravated),
                SeverityLabel: DevicePriorityNarrative.SeverityLabel(k.SeverityOrder, k.Cvss),
                Severity: t.Severity,
                CvssScore: t.CvssScore,
                ExploitLabel: DevicePriorityNarrative.ExploitLabel(t.PublicExploit, t.ExploitVerified),
                AttackVectorLabel: DevicePriorityNarrative.AttackVectorLabel(t.CvssVector),
                Epss: t.Epss,
                KnownExploitedMarkedWithoutOrigin: t.KnownExploited,
                Source: connector.Label,
                FirstSeenAt: p.FirstSeenAt,
                AcquiredAt: p.LastSeenAt,
                AcquisitionState: acquisition,
                AcquisitionLabel: CrossSourceNarrative.AcquisitionLabel(acquisition))));
        }
        return new CasePage(new DevicePriorityCasePageDto(total, page, size, items.Select(i => i.Item2).ToList()), items);
    }

    // ---- Projeção ---------------------------------------------------------------------------------------------------

    private static string BandOf(DevicePriorityAssessment a) =>
        a.Band is { } b ? DevicePriorityBands.Of(b) : DevicePriorityBands.Insufficient;

    private static DevicePriorityItemDto ToItem(
        AssetInfo asset, DevicePriorityAssessment a, DevicePriorityCaseFacts? facts, DevicePriorityCaseDto? determining,
        int position, int ties) =>
        new(
            AssetId: asset.Id,
            AssetName: asset.Name,
            NameIsPlaceholder: asset.NameOrigin == AssetNameOrigin.Placeholder,
            Position: position,
            Status: a.Status,
            Band: BandOf(a),
            BandLabel: DevicePriorityNarrative.StatusBandLabel(a),
            PositionReason: DevicePriorityNarrative.PositionReason(a, facts),
            DeterminingCase: determining,
            PrioritizableCases: a.PrioritizableCases,
            CasesByBand: DevicePriorityBands.Prioritized
                .Select(b => new DevicePriorityCountDto(b, DevicePriorityNarrative.BandLabel(b),
                    a.CasesByBand.GetValueOrDefault(DevicePriorityBands.RankOf(b)!.Value)))
                .Where(c => c.Count > 0)
                .ToList(),
            InsufficientCases: a.InsufficientCases,
            DispositionCases: a.DispositionCases,
            Aggravators: DevicePriorityNarrative.Aggravators(a),
            DeviceContextLabel: DevicePriorityNarrative.DeviceContextLabel(a),
            CriticalityLabel: DevicePriorityNarrative.CriticalityLabel(asset.CriticalityFacts, a.CriticalityState),
            InformationLabel: DevicePriorityNarrative.InformationLabel(a, facts),
            Caveats: Caveats(a, facts),
            NextAction: DevicePriorityNarrative.NextAction(a, facts),
            TiedAssets: ties);

    /// <summary>
    /// Ressalvas das evidências que SUSTENTAM a prioridade: as da fonte de vulnerabilidades (mesma autoridade e textos da
    /// correlação), as das situações usadas como agravante, a indisponibilidade do contexto de gestão e a divergência de
    /// severidade do caso determinante.
    /// </summary>
    private static IReadOnlyList<CrossSourceNoteDto> Caveats(DevicePriorityAssessment a, DevicePriorityCaseFacts? d)
    {
        var notes = new List<CrossSourceNoteDto>();
        void Add(CrossSourceNoteDto n)
        {
            if (!notes.Any(x => x.Code == n.Code && x.Text == n.Text)) notes.Add(n);
        }
        foreach (var n in a.Source.Caveats) Add(n);
        if (a.DeviceContext == DevicePriorityDeviceContexts.Gap)
            foreach (var n in a.IdentifiedSituations.SelectMany(r => r.Caveats)) Add(n);
        if (a.DeviceContext == DevicePriorityDeviceContexts.Contradictory)
            Add(new CrossSourceNoteDto("deviceContextContradictory",
                "Registros do Microsoft Intune para o mesmo dispositivo informam fatos divergentes: nenhum foi escolhido e o " +
                "contexto de gestão não agrava nem atenua a prioridade."));
        if (a.DeviceContext == DevicePriorityDeviceContexts.Conflict)
            Add(new CrossSourceNoteDto("deviceContextConflict",
                "Há registro deste ativo em conflito de vínculo: o contexto do Microsoft Intune não é usado até a associação " +
                "ser resolvida; as vulnerabilidades do Defender continuam atribuídas ao registro dele."));
        if (d is { Cvss: { } cvss, Severity: { } sev } && !string.IsNullOrWhiteSpace(sev))
        {
            var fromText = DevicePriorityPolicy.RankOf(null, sev, null, null).SeverityOrder;
            var fromCvss = DevicePriorityPolicy.RankOf(cvss, null, null, null).SeverityOrder;
            if (fromText != DevicePrioritySeverity.Unknown && fromText != fromCvss)
                Add(new CrossSourceNoteDto("severityDivergence",
                    $"{d.CveId}: a severidade textual da fonte ({DevicePriorityNarrative.SeverityName(fromText, null)}) difere da " +
                    $"faixa do CVSS {DevicePriorityNarrative.Cvss(cvss)} ({DevicePriorityNarrative.SeverityName(fromCvss, cvss)}); " +
                    "a política usa a faixa do CVSS (precedência fixa)."));
        }
        return notes;
    }

    private static IReadOnlyList<DevicePriorityDispositionDto> Dispositions(IEnumerable<KeyValuePair<ExposureStatus, int>> counts)
    {
        var sum = counts.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.Sum(c => c.Value));
        return new[]
            {
                (ExposureStatus.Mitigated, "mitigated", "Mitigação informada"),
                (ExposureStatus.Accepted, "accepted", "Risco aceito"),
                (ExposureStatus.FalsePositive, "falsePositive", "Falso positivo"),
            }
            .Select(d => new DevicePriorityDispositionDto(d.Item2, d.Item3, sum.GetValueOrDefault(d.Item1)))
            .ToList();
    }

    private static (string State, string? Note) Reading(IEnumerable<CrossSourceConnectorFacts> connectors)
    {
        var vuln = connectors.Where(c => c.Role == CrossSourceRole.Vulnerabilities).ToList();
        if (vuln.Count == 0)
            return (DevicePriorityReadingStates.NoSource,
                "Nenhuma integração do Microsoft Defender (vulnerabilidades) está configurada neste ambiente — sem ela não há " +
                "casos a priorizar. Ausência de fonte não é ausência de vulnerabilidade.");
        if (vuln.All(c => c.Watermark is null))
            return (DevicePriorityReadingStates.NeverCollected,
                "A integração do Microsoft Defender está configurada, mas ainda não publicou uma leitura por dispositivo.");
        return (DevicePriorityReadingStates.Available, null);
    }

    private Task HitAsync(string checkpoint, CancellationToken ct) =>
        Checkpoint is null ? Task.CompletedTask : Checkpoint(checkpoint, ct);
}
