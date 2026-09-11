using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Queries;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Queries;

/// <summary>
/// [AEGIS-ENTITY-RESOLUTION-01] Leitura das fontes de um ativo e do vínculo entre elas, sob o Global Query Filter
/// (fail-closed): um ativo de outro tenant simplesmente não existe aqui. Somente leitura — nunca cria, move ou funde
/// binding. A linguagem vem de <see cref="AssetCrossSourceNarrative"/>; identificadores técnicos só no diagnóstico.
/// </summary>
public sealed class AssetSourceQuery : IAssetSourceQuery
{
    /// <summary>Teto de registros de fonte devolvidos por ativo (o excedente é declarado, nunca omitido em silêncio).</summary>
    internal const int MaxSourcesPerAsset = 50;

    /// <summary>Teto de ativos por pedido de resumo (a página da listagem já é limitada a 200).</summary>
    private const int MaxSummaryAssets = 200;

    private const string FallbackSourceLabel = "Fonte integrada";

    private readonly AegisScoreDbContext _db;

    public AssetSourceQuery(AegisScoreDbContext db) => _db = db;

    private sealed record BindingRow(
        Guid AssetId, Guid ConnectorConfigId, string? SourceLabel, bool IsActive,
        AssetBindingResolutionState ResolutionState, DateTimeOffset LastObservedAt);

    public async Task<IReadOnlyDictionary<Guid, AssetSourceSummaryDto>> GetSummariesAsync(
        IReadOnlyCollection<Guid> assetIds, CancellationToken ct = default)
    {
        var ids = assetIds.Distinct().Take(MaxSummaryAssets).ToList();
        var result = new Dictionary<Guid, AssetSourceSummaryDto>();
        if (ids.Count == 0) return result;

        var rows = await _db.AssetSourceBindings.AsNoTracking()
            .Where(b => ids.Contains(b.AssetId))
            .Select(b => new BindingRow(b.AssetId, b.ConnectorConfigId, b.SourceLabel, b.IsActive, b.ResolutionState, b.LastObservedAt))
            .ToListAsync(ct);
        var names = await ConnectorNamesAsync(rows.Select(r => r.ConnectorConfigId), ct);
        var byAsset = rows.GroupBy(r => r.AssetId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var id in ids)
        {
            var list = byAsset.TryGetValue(id, out var found) ? found : new List<BindingRow>();
            var facts = list.Select(r => new AssetBindingFacts(r.ConnectorConfigId, r.IsActive, r.ResolutionState)).ToList();
            var state = AssetCrossSourceNarrative.Derive(facts);
            var active = list.Where(r => r.IsActive).ToList();
            result[id] = new AssetSourceSummaryDto(
                ActiveSourceCount: active.Select(r => r.ConnectorConfigId).Distinct().Count(),
                ActiveSources: active
                    .Select(r => Label(r.SourceLabel, r.ConnectorConfigId, names))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToList(),
                SourceRecords: list.Count,
                CrossSourceState: state,
                CrossSourceLabel: AssetCrossSourceNarrative.Label(state),
                LastObservedAt: list.Count == 0 ? null : list.Max(r => r.LastObservedAt));
        }
        return result;
    }

    public async Task<AssetSourcesDto?> GetSourcesAsync(Guid assetId, bool includeDiagnostics, CancellationToken ct = default)
    {
        var asset = await _db.Assets.AsNoTracking()
            .Where(a => a.Id == assetId)
            .Select(a => new { a.Id, a.Name, a.NameOrigin })
            .FirstOrDefaultAsync(ct);
        if (asset is null) return null;

        var bindings = await _db.AssetSourceBindings.AsNoTracking()
            .Where(b => b.AssetId == assetId)
            .ToListAsync(ct);
        var names = await ConnectorNamesAsync(bindings.Select(b => b.ConnectorConfigId), ct);

        var relatedIds = bindings
            .Where(b => b.ConflictAssetId is not null)
            .Select(b => b.ConflictAssetId!.Value)
            .Distinct()
            .ToList();
        var related = relatedIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Assets.AsNoTracking()
                .Where(a => relatedIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.Name, ct);

        var facts = bindings.Select(b => new AssetBindingFacts(b.ConnectorConfigId, b.IsActive, b.ResolutionState)).ToList();
        var state = AssetCrossSourceNarrative.Derive(facts);
        var linkedSources = AssetCrossSourceNarrative.LinkedSourceCount(facts);

        var shown = bindings
            .OrderByDescending(b => b.IsActive)
            .ThenByDescending(b => b.LastObservedAt)
            .ThenBy(b => Label(b.SourceLabel, b.ConnectorConfigId, names), StringComparer.Ordinal)
            .ThenBy(b => b.ExternalId, StringComparer.Ordinal)
            .Take(MaxSourcesPerAsset)
            .Select(b => ToRecord(b, names, related, includeDiagnostics))
            .ToList();

        return new AssetSourcesDto(
            AssetId: asset.Id,
            AssetName: asset.Name,
            NameIsPlaceholder: asset.NameOrigin == AssetNameOrigin.Placeholder,
            CrossSourceState: state,
            CrossSourceLabel: AssetCrossSourceNarrative.Label(state),
            Explanation: AssetCrossSourceNarrative.Explanation(state, linkedSources),
            LinkMeaning: AssetCrossSourceNarrative.LinkMeaning,
            DirectoryNote: AssetCrossSourceNarrative.DirectoryNote,
            CuratedNote: AssetCrossSourceNarrative.CuratedNote,
            ActiveSourceCount: bindings.Where(b => b.IsActive).Select(b => b.ConnectorConfigId).Distinct().Count(),
            SourceRecords: bindings.Count,
            Truncated: bindings.Count > MaxSourcesPerAsset,
            DiagnosticsIncluded: includeDiagnostics,
            Sources: shown);
    }

    private static AssetSourceRecordDto ToRecord(
        AssetSourceBinding b, IReadOnlyDictionary<Guid, string> names,
        IReadOnlyDictionary<Guid, string> related, bool includeDiagnostics)
    {
        var (label, explanation) = AssetCrossSourceNarrative.ForBinding(b.ResolutionState, b.ConflictKind, b.DirectoryIdStatus);
        var relatedName = b.ConflictAssetId is { } rid && related.TryGetValue(rid, out var n) ? n : null;
        return new AssetSourceRecordDto(
            SourceLabel: Label(b.SourceLabel, b.ConnectorConfigId, names),
            IsActive: b.IsActive,
            PresenceLabel: b.IsActive ? "Presente na última leitura da fonte" : "Não mais observado pela fonte",
            FirstObservedAt: b.FirstObservedAt,
            LastObservedAt: b.LastObservedAt,
            SourceLastSeenAt: b.SourceLastSeenAt,
            NoLongerObservedSince: b.IsActive ? null : b.ResolvedAt,
            ObservedName: b.DisplayName,
            Platform: b.SubType,
            ResolutionState: b.ResolutionState.ToString(),
            ResolutionLabel: label,
            ResolutionExplanation: explanation,
            IdentifierStatus: b.DirectoryIdStatus.ToString(),
            IdentifierStatusLabel: AssetCrossSourceNarrative.IdentifierStatusLabel(b.DirectoryIdStatus),
            ConflictKind: b.ResolutionState == AssetBindingResolutionState.Conflict ? b.ConflictKind.ToString() : null,
            RelatedAssetId: b.ConflictAssetId,
            RelatedAssetName: relatedName,
            SourceCompliance: b.SourceCompliance?.ToString(),
            SourceComplianceLabel: AssetCrossSourceNarrative.ComplianceLabel(b.SourceCompliance),
            SourceEncryption: b.SourceEncryption?.ToString(),
            SourceEncryptionLabel: AssetCrossSourceNarrative.EncryptionLabel(b.SourceEncryption),
            Diagnostics: includeDiagnostics
                ? new AssetSourceDiagnosticsDto(
                    b.ExternalId, b.DirectoryNamespace, b.DirectoryDeviceId, b.ConflictDirectoryDeviceId,
                    b.LinkedAt, b.ResolutionEvaluatedAt)
                : null);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> ConnectorNamesAsync(IEnumerable<Guid> connectorIds, CancellationToken ct)
    {
        var ids = connectorIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();
        return await _db.Connectors.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.DisplayName, ct);
    }

    private static string Label(string? sourceLabel, Guid connectorId, IReadOnlyDictionary<Guid, string> names) =>
        !string.IsNullOrWhiteSpace(sourceLabel) ? sourceLabel!
        : names.TryGetValue(connectorId, out var n) && !string.IsNullOrWhiteSpace(n) ? n
        : FallbackSourceLabel;
}
