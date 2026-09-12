using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Queries;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Queries;

/// <summary>Ativo lido para a avaliação entre fontes (sem identificadores técnicos).</summary>
internal sealed record CrossSourceAssetRow(Guid Id, string Name, AssetNameOrigin NameOrigin);

/// <summary>
/// [AEGIS-CROSS-SOURCE-01 · AEGIS-RISK-PRIORITIZATION-01] Aquisição dos fatos que a autoridade de correlação avalia — a
/// MESMA leitura para as situações entre fontes e para a prioridade de tratamento, de modo que as duas superfícies não
/// possam divergir sobre registros, marcas, desfechos ou observações. Aqui não há regra: o banco só filtra por tenant
/// (Global Query Filter), agrega observações por (ativo, conector, ciclo de vida, marca) e devolve os registros.
/// </summary>
internal static class CrossSourceFactReader
{
    internal const string VulnerabilitiesLabel = "Microsoft Defender Vulnerability Management";
    internal const string DeviceManagementLabel = "Microsoft Intune";
    private const int Chunk = 500;

    /// <summary>Conectores das duas fontes das regras, com a marca, o desfecho e a última tentativa.</summary>
    internal static async Task<Dictionary<Guid, CrossSourceConnectorFacts>> LoadConnectorsAsync(
        AegisScoreDbContext db, CancellationToken ct)
    {
        var rows = await db.Connectors.AsNoTracking()
            .Where(c => c.Provider == ConnectorProvider.Microsoft
                && (c.Capability == ConnectorCapability.VulnerabilityScanner || c.Capability == ConnectorCapability.ConfigAnalyzer))
            .Select(c => new
            {
                c.Id, c.Provider, c.Capability, c.DisplayName, c.DeviceSnapshotWatermark, c.DeviceSnapshotOutcome, c.LastStatus,
            })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var postures = ids.Count == 0
            ? new Dictionary<Guid, (DevicePostureDimensionState State, DateTimeOffset? At)>()
            : (await db.DevicePostureSnapshots.AsNoTracking()
                    .Where(s => ids.Contains(s.ConnectorConfigId))
                    .Select(s => new { s.ConnectorConfigId, s.DeviceAttemptState, s.DeviceAttemptAt })
                    .ToListAsync(ct))
                .ToDictionary(s => s.ConnectorConfigId, s => (State: s.DeviceAttemptState, At: s.DeviceAttemptAt));

        var result = new Dictionary<Guid, CrossSourceConnectorFacts>();
        foreach (var r in rows)
        {
            var role = CrossSourceSources.RoleOf(r.Provider, r.Capability)!.Value;
            var failed = r.LastStatus == ConnectorStatus.Failed;
            DateTimeOffset? failedAt = null;
            // Gestão de dispositivos: a DIMENSÃO de dispositivos registra a própria tentativa (pode falhar sem Failed no
            // conector, ex.: permissão ausente). A tentativa descreve a tentativa — nunca a completude dos fatos guardados.
            if (role == CrossSourceRole.DeviceManagement && postures.TryGetValue(r.Id, out var p)
                && p.State is DevicePostureDimensionState.NotAuthorized or DevicePostureDimensionState.NotLicensed
                    or DevicePostureDimensionState.Unavailable)
            {
                failed = true;
                failedAt = p.At;
            }
            result[r.Id] = new CrossSourceConnectorFacts(
                r.Id, role,
                role == CrossSourceRole.Vulnerabilities ? VulnerabilitiesLabel : DeviceManagementLabel,
                string.IsNullOrWhiteSpace(r.DisplayName) ? "Integração sem nome" : r.DisplayName,
                r.DeviceSnapshotWatermark, r.DeviceSnapshotOutcome, failed, failedAt);
        }
        return result;
    }

    /// <summary>
    /// Registros de fonte, chaves fortes e observações AGREGADAS dos ativos pedidos, em lotes — nunca a lista de
    /// observações por CVE. Todos os ativos pedidos recebem uma entrada.
    /// </summary>
    internal static async Task<Dictionary<Guid, CrossSourceAssetFacts>> LoadFactsAsync(
        AegisScoreDbContext db, IReadOnlyList<CrossSourceAssetRow> assets,
        IReadOnlyDictionary<Guid, CrossSourceConnectorFacts> connectors, CancellationToken ct)
    {
        var result = new Dictionary<Guid, CrossSourceAssetFacts>();
        var vulnIds = connectors.Values
            .Where(c => c.Role == CrossSourceRole.Vulnerabilities)
            .Select(c => c.ConnectorId)
            .ToList();

        foreach (var chunk in assets.Chunk(Chunk))
        {
            var ids = chunk.Select(a => a.Id).ToList();
            var bindings = await db.AssetSourceBindings.AsNoTracking()
                .Where(b => ids.Contains(b.AssetId))
                .Select(b => new
                {
                    b.AssetId, b.Id, b.ConnectorConfigId, b.SourceLabel, b.IsActive, b.ResolutionState, b.ConflictKind,
                    b.DirectoryNamespace, b.DirectoryDeviceId, b.FirstObservedAt, b.LastObservedAt, b.SourceLastSeenAt,
                    b.ResolvedAt, b.LinkedAt, b.SourceCompliance, b.SourceEncryption,
                })
                .ToListAsync(ct);
            var keys = await db.AssetStrongIdentifiers.AsNoTracking()
                .Where(k => ids.Contains(k.AssetId) && k.IdentifierType == AssetStrongIdentifierType.EntraDeviceId)
                .Select(k => new { k.AssetId, k.DirectoryNamespace, k.IdentifierValue })
                .ToListAsync(ct);
            var groups = await (
                    from o in db.AssetThreatObservations.AsNoTracking()
                    join e in db.AssetThreatExposures.AsNoTracking() on o.AssetThreatExposureId equals e.Id
                    where ids.Contains(e.AssetId) && vulnIds.Contains(o.ConnectorConfigId)
                    group o by new { e.AssetId, o.ConnectorConfigId, o.LifecycleState, o.LastSeenAt } into g
                    select new { g.Key.AssetId, g.Key.ConnectorConfigId, g.Key.LifecycleState, g.Key.LastSeenAt, Count = g.Count() })
                .ToListAsync(ct);

            var bindingsByAsset = bindings.ToLookup(b => b.AssetId);
            var keysByAsset = keys.ToLookup(k => k.AssetId);
            var groupsByAsset = groups.ToLookup(g => g.AssetId);
            foreach (var a in chunk)
            {
                result[a.Id] = new CrossSourceAssetFacts(
                    a.Id,
                    a.Name,
                    a.NameOrigin == AssetNameOrigin.Placeholder,
                    keysByAsset[a.Id].Select(k => new CrossSourceKey(k.DirectoryNamespace, k.IdentifierValue)).ToList(),
                    bindingsByAsset[a.Id].Select(b => new CrossSourceBindingFacts(
                        b.Id, b.ConnectorConfigId, b.SourceLabel, b.IsActive, b.ResolutionState, b.ConflictKind,
                        b.DirectoryNamespace, b.DirectoryDeviceId, b.FirstObservedAt, b.LastObservedAt, b.SourceLastSeenAt,
                        b.ResolvedAt, b.LinkedAt, b.SourceCompliance, b.SourceEncryption)).ToList(),
                    connectors,
                    groupsByAsset[a.Id]
                        .Select(g => new CrossSourceObservationGroup(g.ConnectorConfigId, g.LifecycleState, g.LastSeenAt, g.Count))
                        .ToList());
            }
        }
        return result;
    }

    /// <summary>
    /// Executa a leitura numa ÚNICA transação somente leitura — <c>REPEATABLE READ</c> no PostgreSQL, para que todos os
    /// SELECTs vejam a mesma fotografia do banco. Se já houver transação no contexto, participa dela.
    /// </summary>
    internal static async Task<T> InReadSnapshotAsync<T>(AegisScoreDbContext db, Func<Task<T>> read, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) return await read();
        var npgsql = db.Database.IsNpgsql();
        await using var tx = npgsql
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
            : await db.Database.BeginTransactionAsync(ct);
        if (npgsql) await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        var result = await read();
        await tx.CommitAsync(ct);
        return result;
    }
}
