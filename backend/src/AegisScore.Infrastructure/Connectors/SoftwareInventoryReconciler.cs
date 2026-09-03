using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Connectors;

/// <summary>
/// [AEGIS-MVP-MICROSOFT-COVERAGE-01] Reconcilia uma coleta de inventário de software de UMA fonte. Combina os DOIS
/// padrões já estabelecidos neste código-base:
///  • upsert idempotente + resolução SOMENTE em coleta completa (idioma do <see cref="VulnerabilityReconciler"/>),
///    para produtos/bindings/instalações;
///  • preservação HONESTA de dados válidos numa falha/parcial + snapshot de estado/última tentativa (idioma do
///    <see cref="DetectionCoverageReconciler"/>), para o <see cref="SoftwareInventorySnapshot"/> agregado.
///
/// Reusa as <see cref="AssetSourceBinding"/> JÁ normalizadas pela dimensão de máquinas/vulnerabilidades desta MESMA
/// sincronização (chamado DEPOIS de <c>ReconcileVulnerabilitiesAsync</c> no executor) — nunca cria Asset por conta
/// própria; uma instalação cujo dispositivo não tem binding ativo do MESMO conector é tratada como órfã (inválida).
///
/// NUNCA cria EvidenceSignal, NUNCA toca TenantControlState/score/NIST, NUNCA persiste payload bruto/hostname.
/// </summary>
public sealed class SoftwareInventoryReconciler
{
    private const string ProductNaturalIndex = "UX_SoftwareProduct_Natural";
    private const string BindingNaturalIndex = "UX_SoftwareProductSourceBinding_Natural";
    private const string InstallationNaturalIndex = "UX_SoftwareInstallation_Natural";

    private const int InstallationBatchSize = 500;
    private const int ProductBatchSize = 250;

    private readonly AegisScoreDbContext _db;
    private readonly ILogger? _log;

    public SoftwareInventoryReconciler(AegisScoreDbContext db, ILogger? log = null)
    {
        _db = db;
        _log = log;
    }

    public async Task<SoftwareInventorySyncResult> ReconcileAsync(
        Guid connectorId, SoftwareInventoryCollection incoming, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await ReconcileAttemptAsync(connectorId, incoming, ct);
            }
            catch (DbUpdateException ex) when (attempt == 0 && IsExpectedTenantRace(ex))
            {
                _db.ChangeTracker.Clear();
                _log?.LogInformation(
                    "Corrida de inserção na reconciliação de inventário de software do conector {ConnectorId} — recarregando e reaplicando.",
                    connectorId);
            }
        }

        throw new InvalidOperationException(
            "Reconciliação de inventário de software falhou após recuperar a corrida de inserção.");
    }

    private async Task<SoftwareInventorySyncResult> ReconcileAttemptAsync(
        Guid connectorId, SoftwareInventoryCollection incoming, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // (0) Falha CLASSIFICADA sem nenhum dado utilizável: NUNCA sobrescreve produtos/instalações já persistidos —
        // só registra a tentativa e preserva o estado/dados anteriores. Espelha o branch 1 do DetectionCoverageReconciler.
        if (incoming.State is SoftwareInventoryCollectionState.InsufficientPermission
            or SoftwareInventoryCollectionState.Unsupported
            or SoftwareInventoryCollectionState.Unavailable)
        {
            await StampAttemptOnlyAsync(connectorId, incoming, now, ct);
            _log?.LogInformation(
                "Inventário de software do conector {ConnectorId}: tentativa {State} registrada (dados preservados).",
                connectorId, incoming.State);
            return new SoftwareInventorySyncResult(incoming.State, 0, 0, 0, 0, 0, 0, false, incoming.InvalidProducts, incoming.InvalidInstallations);
        }

        // (1) Produtos: upsert idempotente por (Tenant, Conector, ExternalProductId) — chave do BINDING de fonte.
        // O produto CONSOLIDADO usa a identidade natural (Vendor, Nome) normalizada, compartilhada entre fontes.
        var existingBindings = await _db.SoftwareProductSourceBindings
            .Where(b => b.ConnectorConfigId == connectorId)
            .ToListAsync(ct);
        var bindingByExternalId = existingBindings
            .GroupBy(b => b.ExternalProductId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var externalIdToProductId = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var naturalKeyToProductId = new Dictionary<(string VendorKey, string NameKey), Guid>();
        var productsCreated = 0;
        var seenExternalIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var batch in incoming.Products.Chunk(ProductBatchSize))
        {
            // Filtra por VENDOR (superset — pode trazer produtos do mesmo vendor com outro nome); o par EXATO
            // (VendorKey, NameKey) é resolvido pela chave da dictionary abaixo, não pela query. `.Contains` numa
            // tupla não é traduzível de forma portátil por todos os provedores EF, e o superset já é pequeno
            // (ProductBatchSize é limitado).
            var vendorKeysInBatch = batch.Select(p => NormalizeKey(p.Vendor)).Distinct().ToList();
            var existingProducts = await _db.SoftwareProducts
                .Where(sp => vendorKeysInBatch.Contains(sp.VendorKey))
                .ToListAsync(ct);
            var productByKey = existingProducts
                .GroupBy(p => (p.VendorKey, p.NameKey))
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var fact in batch)
            {
                seenExternalIds.Add(fact.ExternalProductId);
                var vendor = fact.Vendor ?? "";
                var name = fact.Name ?? "";
                var natKey = (VendorKey: NormalizeKey(vendor), NameKey: NormalizeKey(name));

                if (!productByKey.TryGetValue(natKey, out var product))
                {
                    product = new SoftwareProduct
                    {
                        Vendor = TrimTo(vendor, 200) ?? "",
                        Name = TrimTo(name, 300) ?? "",
                        VendorKey = TrimTo(natKey.VendorKey, 200) ?? "",
                        NameKey = TrimTo(natKey.NameKey, 300) ?? "",
                        IsActive = true,
                        FirstSeenAt = now,
                        LastSeenAt = now,
                    };
                    _db.SoftwareProducts.Add(product);
                    productByKey[natKey] = product;
                    productsCreated++;
                }
                else
                {
                    product.LastSeenAt = now;
                }

                if (bindingByExternalId.TryGetValue(fact.ExternalProductId, out var binding))
                {
                    binding.SoftwareProductId = product.Id;
                    ApplyBindingFacts(binding, fact, now);
                }
                else
                {
                    binding = new SoftwareProductSourceBinding
                    {
                        SoftwareProductId = product.Id,
                        ConnectorConfigId = connectorId,
                        ExternalProductId = fact.ExternalProductId,
                        FirstObservedAt = now,
                    };
                    ApplyBindingFacts(binding, fact, now);
                    _db.SoftwareProductSourceBindings.Add(binding);
                    bindingByExternalId[fact.ExternalProductId] = binding;
                }

                naturalKeyToProductId[natKey] = product.Id;
                externalIdToProductId[fact.ExternalProductId] = product.Id;
            }

            await _db.SaveChangesAsync(ct);
        }
        _db.ChangeTracker.Clear();

        // (2) Instalações: upsert por (Tenant, Conector, AssetId, ProductId, Version). O Asset vem do
        // AssetSourceBinding JÁ normalizado por esta MESMA fonte (dimensão de máquinas) — nunca criado aqui.
        var machineIds = incoming.Installations.Select(i => i.MachineId).Distinct(StringComparer.Ordinal).ToList();
        var assetBindings = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var idBatch in machineIds.Chunk(InstallationBatchSize))
        {
            var rows = await _db.AssetSourceBindings.AsNoTracking()
                .Where(b => b.ConnectorConfigId == connectorId && idBatch.Contains(b.ExternalId))
                .Select(b => new { b.ExternalId, b.AssetId })
                .ToListAsync(ct);
            foreach (var row in rows) assetBindings[row.ExternalId] = row.AssetId;
        }

        var installationsOpened = 0;
        var installationsReopened = 0;
        var orphanInstallations = 0;

        foreach (var machineGroup in incoming.Installations.GroupBy(i => i.MachineId, StringComparer.Ordinal))
        {
            if (!assetBindings.TryGetValue(machineGroup.Key, out var assetId)) { orphanInstallations += machineGroup.Count(); continue; }

            foreach (var batch in machineGroup.Chunk(InstallationBatchSize))
            {
                var productIdsInBatch = batch
                    .Select(i => naturalKeyToProductId.TryGetValue((NormalizeKey(i.Vendor), NormalizeKey(i.Name)), out var pid) ? pid : (Guid?)null)
                    .Where(pid => pid.HasValue)
                    .Select(pid => pid!.Value)
                    .Distinct()
                    .ToList();
                if (productIdsInBatch.Count == 0) continue;

                var existingInstalls = await _db.SoftwareInstallations
                    .Where(si => si.ConnectorConfigId == connectorId && si.AssetId == assetId && productIdsInBatch.Contains(si.SoftwareProductId))
                    .ToListAsync(ct);
                var installByKey = existingInstalls
                    .GroupBy(si => (si.SoftwareProductId, si.Version), (k, g) => (k, First: g.First()))
                    .ToDictionary(x => x.k, x => x.First);

                foreach (var fact in batch)
                {
                    if (!naturalKeyToProductId.TryGetValue((NormalizeKey(fact.Vendor), NormalizeKey(fact.Name)), out var productId))
                    { orphanInstallations++; continue; }

                    var version = fact.Version ?? "";
                    var key = (productId, version);
                    if (installByKey.TryGetValue(key, out var existing))
                    {
                        existing.LastSeenAt = now;
                        if (existing.LifecycleState == ObservationLifecycle.Resolved) installationsReopened++;
                        existing.LifecycleState = ObservationLifecycle.Open;
                        existing.ResolvedAt = null;
                    }
                    else
                    {
                        var created = new SoftwareInstallation
                        {
                            SoftwareProductId = productId,
                            AssetId = assetId,
                            ConnectorConfigId = connectorId,
                            Version = TrimTo(version, 100) ?? "",
                            LifecycleState = ObservationLifecycle.Open,
                            FirstSeenAt = now,
                            LastSeenAt = now,
                        };
                        _db.SoftwareInstallations.Add(created);
                        installByKey[key] = created;
                        installationsOpened++;
                    }
                }

                await _db.SaveChangesAsync(ct);
                _db.ChangeTracker.Clear();
            }
        }

        // (3) FAIL-CLOSED: resolução/desativação por omissão só em coleta COMPLETA (Available) — nunca em Partial.
        var installationsResolved = 0;
        var bindingsDeactivated = 0;
        if (incoming.State == SoftwareInventoryCollectionState.Available)
        {
            installationsResolved = await _db.SoftwareInstallations
                .Where(si => si.ConnectorConfigId == connectorId && si.LifecycleState != ObservationLifecycle.Resolved && si.LastSeenAt != now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(si => si.LifecycleState, ObservationLifecycle.Resolved)
                    .SetProperty(si => si.ResolvedAt, now), ct);

            var staleBindingIds = await _db.SoftwareProductSourceBindings
                .Where(b => b.ConnectorConfigId == connectorId && b.IsActive && !seenExternalIds.Contains(b.ExternalProductId))
                .Select(b => b.Id)
                .ToListAsync(ct);
            if (staleBindingIds.Count > 0)
            {
                bindingsDeactivated = await _db.SoftwareProductSourceBindings
                    .Where(b => staleBindingIds.Contains(b.Id))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(b => b.IsActive, false)
                        .SetProperty(b => b.ResolvedAt, now), ct);
            }
        }
        _db.ChangeTracker.Clear();

        // (4) Recompute agregado dos produtos TOCADOS (produtos criados/atualizados nesta coleta + os desativados
        // acima), a partir dos bindings ATIVOS de TODAS as fontes — mesmo idioma do RecomputeAssetsBatchedAsync.
        var touchedProductIds = new HashSet<Guid>(naturalKeyToProductId.Values);
        await RecomputeProductsBatchedAsync(touchedProductIds, ct);

        // (5) Snapshot agregado do conector: cache de KPIs + estado/última tentativa/última coleta.
        await UpsertSnapshotAsync(connectorId, incoming, now, ct);

        if (orphanInstallations > 0)
            _log?.LogInformation(
                "Inventário de software do conector {ConnectorId}: {Orphans} instalação(ões) órfã(s) (produto/máquina fora da fotografia) ignorada(s).",
                connectorId, orphanInstallations);

        var wasComplete = incoming.State == SoftwareInventoryCollectionState.Available;
        return new SoftwareInventorySyncResult(
            incoming.State, incoming.Products.Count, productsCreated, bindingsDeactivated,
            installationsOpened, installationsReopened, installationsResolved, wasComplete,
            incoming.InvalidProducts, incoming.InvalidInstallations);
    }

    private static void ApplyBindingFacts(SoftwareProductSourceBinding b, SoftwareProductFact fact, DateTimeOffset now)
    {
        b.VendorObserved = TrimTo(fact.Vendor, 200);
        b.NameObserved = TrimTo(fact.Name, 300);
        b.Weaknesses = fact.Weaknesses ?? 0;
        b.PublicExploit = fact.PublicExploit ?? false;
        b.ActiveAlert = fact.ActiveAlert ?? false;
        b.ExposedMachines = fact.ExposedMachines ?? 0;
        b.ImpactScore = fact.ImpactScore;
        b.LastObservedAt = now;
        b.IsActive = true;
        b.ResolvedAt = null;
    }

    private async Task RecomputeProductsBatchedAsync(HashSet<Guid> productIds, CancellationToken ct)
    {
        if (productIds.Count == 0) return;

        foreach (var idBatch in productIds.Chunk(ProductBatchSize))
        {
            var products = await _db.SoftwareProducts.Where(p => idBatch.Contains(p.Id)).ToListAsync(ct);
            var activeBindings = await _db.SoftwareProductSourceBindings.AsNoTracking()
                .Where(b => idBatch.Contains(b.SoftwareProductId) && b.IsActive)
                .ToListAsync(ct);
            var bindingsByProduct = activeBindings.GroupBy(b => b.SoftwareProductId).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var product in products)
            {
                var hasActive = bindingsByProduct.TryGetValue(product.Id, out var found) && found.Count > 0;
                product.IsActive = hasActive;
                if (hasActive)
                {
                    var bs = found!;
                    product.WeaknessesCount = bs.Max(b => b.Weaknesses);
                    product.HasPublicExploit = bs.Any(b => b.PublicExploit);
                    product.HasActiveAlert = bs.Any(b => b.ActiveAlert);
                    product.ExposedMachinesCount = bs.Max(b => b.ExposedMachines);
                    var impactScores = bs.Where(b => b.ImpactScore.HasValue).Select(b => b.ImpactScore!.Value).ToList();
                    product.ImpactScore = impactScores.Count > 0 ? impactScores.Max() : null;
                    var lastSeen = bs.Max(b => b.LastObservedAt);
                    if (product.LastSeenAt < lastSeen) product.LastSeenAt = lastSeen;
                }
            }

            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
        }
    }

    // ---- Snapshot agregado (estado/última tentativa/KPIs) — idioma DetectionCoverageReconciler -----------------

    private async Task UpsertSnapshotAsync(
        Guid connectorId, SoftwareInventoryCollection incoming, DateTimeOffset now, CancellationToken ct)
    {
        var snapshot = await _db.SoftwareInventorySnapshots.FirstOrDefaultAsync(s => s.ConnectorConfigId == connectorId, ct);
        if (snapshot is null)
        {
            snapshot = new SoftwareInventorySnapshot { ConnectorConfigId = connectorId };
            _db.SoftwareInventorySnapshots.Add(snapshot);
        }

        snapshot.Source = incoming.Source;
        snapshot.LastAttemptState = incoming.State;
        snapshot.LastAttemptAt = now;
        snapshot.LastAttemptDetail = TrimTo(incoming.Detail, 1000);

        // CollectionState/KPIs/LastCollectionAt só avançam em Available/Partial (dados armazenados) — nunca em falha
        // total (já tratada antes de chegar aqui) e nunca retrocedem de Available para Partial silenciosamente:
        // uma coleta Partial posterior a uma Available preserva os KPIs/estado completos anteriores.
        if (incoming.State == SoftwareInventoryCollectionState.Available
            || snapshot.CollectionState != SoftwareInventoryCollectionState.Available)
        {
            snapshot.CollectionState = incoming.State;
            snapshot.LastCollectionAt = now;

            var (total, withWeak, withExploit, withAlert, exposedInstalls) = await ComputeKpisAsync(connectorId, ct);
            snapshot.TotalProducts = total;
            snapshot.ProductsWithWeaknesses = withWeak;
            snapshot.ProductsWithPublicExploit = withExploit;
            snapshot.ProductsWithActiveAlert = withAlert;
            snapshot.ExposedInstallations = exposedInstalls;
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task<(int Total, int WithWeaknesses, int WithExploit, int WithAlert, int ExposedInstallations)> ComputeKpisAsync(
        Guid connectorId, CancellationToken ct)
    {
        var activeProductIds = await _db.SoftwareProductSourceBindings.AsNoTracking()
            .Where(b => b.ConnectorConfigId == connectorId && b.IsActive)
            .Select(b => b.SoftwareProductId)
            .Distinct()
            .ToListAsync(ct);
        if (activeProductIds.Count == 0) return (0, 0, 0, 0, 0);

        var products = await _db.SoftwareProducts.AsNoTracking()
            .Where(p => activeProductIds.Contains(p.Id))
            .Select(p => new { p.Id, p.WeaknessesCount, p.HasPublicExploit, p.HasActiveAlert })
            .ToListAsync(ct);

        var exposedInstallations = await _db.SoftwareInstallations.AsNoTracking()
            .Where(si => si.ConnectorConfigId == connectorId
                && si.LifecycleState == ObservationLifecycle.Open
                && activeProductIds.Contains(si.SoftwareProductId))
            .CountAsync(ct);

        return (
            products.Count,
            products.Count(p => p.WeaknessesCount > 0),
            products.Count(p => p.HasPublicExploit),
            products.Count(p => p.HasActiveAlert),
            exposedInstallations);
    }

    private async Task StampAttemptOnlyAsync(
        Guid connectorId, SoftwareInventoryCollection incoming, DateTimeOffset now, CancellationToken ct)
    {
        var snapshot = await _db.SoftwareInventorySnapshots.FirstOrDefaultAsync(s => s.ConnectorConfigId == connectorId, ct);
        if (snapshot is null)
        {
            snapshot = new SoftwareInventorySnapshot
            {
                ConnectorConfigId = connectorId,
                Source = incoming.Source,
                CollectionState = SoftwareInventoryCollectionState.NeverCollected,
            };
            _db.SoftwareInventorySnapshots.Add(snapshot);
        }
        else
        {
            snapshot.Source = incoming.Source;
            // CollectionState/LastCollectionAt/KPIs PRESERVADOS — só a tentativa avança.
        }
        snapshot.LastAttemptState = incoming.State;
        snapshot.LastAttemptAt = now;
        snapshot.LastAttemptDetail = TrimTo(incoming.Detail, 1000);
        await _db.SaveChangesAsync(ct);
    }

    private static string NormalizeKey(string? s) => (s ?? "").Trim().ToLowerInvariant();

    private static string? TrimTo(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        return t.Length <= max ? t : t[..max];
    }

    private static bool IsExpectedTenantRace(DbUpdateException ex) =>
        IsRace(ex, ProductNaturalIndex, "SoftwareProduct")
        || IsRace(ex, BindingNaturalIndex, "SoftwareProductSourceBinding")
        || IsRace(ex, InstallationNaturalIndex, "SoftwareInstallation");

    private static bool IsRace(DbUpdateException ex, string pgConstraint, string sqliteTable)
    {
        if (ex.InnerException is PostgresException pg)
            return pg.SqlState == PostgresErrorCodes.UniqueViolation
                && string.Equals(pg.ConstraintName, pgConstraint, StringComparison.Ordinal);

        var inner = ex.InnerException;
        return inner is not null
            && inner.GetType().Name == "SqliteException"
            && inner.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
            && inner.Message.Contains(sqliteTable, StringComparison.OrdinalIgnoreCase);
    }
}
