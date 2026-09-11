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
/// PRECEDÊNCIA [AEGIS-ENTITY-RESOLUTION-01]. Na aquisição COMBINADA (máquinas, vulnerabilidades e software numa só
/// leitura da fonte), o executor entrega a MARCA dessa aquisição — a mesma que a dimensão de dispositivos publicou como
/// marca da fonte (<see cref="ConnectorConfig.DeviceSnapshotWatermark"/>). Com ela, cada escrita desta dimensão (lotes
/// de produtos/bindings, lotes de instalações, ausência, recálculo dos produtos e resumo) passa pela MESMA coordenação
/// da fonte (<see cref="DeviceIdentityResolver.RunIfCurrentAsync"/>): numa transação, sob a trava da fonte e só se a
/// marca ainda for a publicada. Uma aquisição superada para no primeiro passo recusado — não publica nem reabre
/// instalações, não resolve as da mais recente, não regride fatos de binding e não sobrescreve o resumo. Os instantes
/// gravados são a própria marca, nunca um horário novo da reconciliação. Sem a marca (chamada direta, fora da aquisição
/// combinada), o comportamento anterior é mantido.
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

    // Pontos de observação para testes de intercalação (barreiras). Sem efeito quando Checkpoint é nulo. Só o da
    // ausência fica DENTRO da trava da fonte; os demais ficam entre passos, sem transação aberta.
    internal const string CheckpointStarted = "software-started";
    internal const string CheckpointBatchCommitted = "software-batch-committed";
    internal const string CheckpointBeforeAbsence = "software-before-absence";
    internal const string CheckpointAbsenceLocked = "software-absence-locked";

    private readonly AegisScoreDbContext _db;
    private readonly ILogger? _log;

    public SoftwareInventoryReconciler(AegisScoreDbContext db, ILogger? log = null)
    {
        _db = db;
        _log = log;
    }

    /// <summary>SOMENTE para testes: barreira chamada nos pontos <c>Checkpoint*</c>.</summary>
    internal Func<string, CancellationToken, Task>? Checkpoint { get; init; }

    /// <summary>Reconciliação sem marca de aquisição: comportamento anterior, sem coordenação com a fonte.</summary>
    public Task<SoftwareInventorySyncResult> ReconcileAsync(
        Guid connectorId, SoftwareInventoryCollection incoming, CancellationToken ct) =>
        ReconcileAsync(connectorId, incoming, acquisitionMarker: null, ct);

    /// <summary>
    /// Reconcilia a dimensão de software. <paramref name="acquisitionMarker"/> é a marca da aquisição COMBINADA a que
    /// esta coleta pertence (a publicada pela dimensão de dispositivos da mesma aquisição): com ela, a publicação só
    /// acontece enquanto essa aquisição for a mais recente da fonte, e todos os instantes gravados são essa marca.
    /// </summary>
    public async Task<SoftwareInventorySyncResult> ReconcileAsync(
        Guid connectorId, SoftwareInventoryCollection incoming, DateTimeOffset? acquisitionMarker, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await ReconcileAttemptAsync(connectorId, incoming, acquisitionMarker, ct);
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
        Guid connectorId, SoftwareInventoryCollection incoming, DateTimeOffset? acquisitionMarker, CancellationToken ct)
    {
        // Com a marca, a coordenação é a MESMA da fonte (trava + marca publicada), da autoridade de resolução. A marca é
        // a referência da aquisição original: nunca é comparada com o instante de outra fase (ex.: AttemptedAt do
        // software), nem substituída por um horário novo que faria uma leitura antiga parecer atual.
        var gate = acquisitionMarker is null ? null : new DeviceIdentityResolver(_db, _log);
        var now = acquisitionMarker is { } marker ? DeviceSnapshotMarker.Normalize(marker) : DateTimeOffset.UtcNow;

        var productsUpserted = 0;
        var productsCreated = 0;
        var bindingsDeactivated = 0;
        var installationsOpened = 0;
        var installationsReopened = 0;
        var installationsResolved = 0;
        var orphanInstallations = 0;
        var superseded = false;

        SoftwareInventorySyncResult Result() => new(
            incoming.State, productsUpserted, productsCreated, bindingsDeactivated,
            installationsOpened, installationsReopened, installationsResolved,
            incoming.State == SoftwareInventoryCollectionState.Available,
            incoming.InvalidProducts, incoming.InvalidInstallations, superseded);

        SoftwareInventorySyncResult Stop()
        {
            superseded = true;
            _db.ChangeTracker.Clear();
            _log?.LogInformation(
                "Inventário de software do conector {ConnectorId} superado por uma aquisição mais recente da fonte — publicação interrompida; nada do que a mais recente publicou foi alterado.",
                connectorId);
            return Result();
        }

        await HitAsync(CheckpointStarted, ct);

        // (0) Falha CLASSIFICADA sem nenhum dado utilizável: NUNCA sobrescreve produtos/instalações já persistidos —
        // só registra a tentativa e preserva o estado/dados anteriores. Espelha o branch 1 do DetectionCoverageReconciler.
        if (incoming.State is SoftwareInventoryCollectionState.InsufficientPermission
            or SoftwareInventoryCollectionState.Unsupported
            or SoftwareInventoryCollectionState.Unavailable)
        {
            if (!await PublishAsync(gate, connectorId, now, c => StampAttemptOnlyAsync(connectorId, incoming, now, c), ct))
                return Stop();
            _log?.LogInformation(
                "Inventário de software do conector {ConnectorId}: tentativa {State} registrada (dados preservados).",
                connectorId, incoming.State);
            return Result();
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
        var seenExternalIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var batch in incoming.Products.Chunk(ProductBatchSize))
        {
            var current = await PublishAsync(gate, connectorId, now, async batchCt =>
            {
                // Filtra por VENDOR (superset — pode trazer produtos do mesmo vendor com outro nome); o par EXATO
                // (VendorKey, NameKey) é resolvido pela chave da dictionary abaixo, não pela query. `.Contains` numa
                // tupla não é traduzível de forma portátil por todos os provedores EF, e o superset já é pequeno
                // (ProductBatchSize é limitado).
                var vendorKeysInBatch = batch.Select(p => NormalizeKey(p.Vendor)).Distinct().ToList();
                var existingProducts = await _db.SoftwareProducts
                    .Where(sp => vendorKeysInBatch.Contains(sp.VendorKey))
                    .ToListAsync(batchCt);
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
                    else if (product.LastSeenAt < now)
                    {
                        // O produto é compartilhado entre fontes: a marca de uma aquisição nunca o faz regredir.
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

                await _db.SaveChangesAsync(batchCt);
                productsUpserted += batch.Length;
            }, ct);
            if (!current) return Stop();
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

                // Cada lote é publicado só enquanto esta aquisição for a mais recente: uma superada nunca reabre o que a
                // mais nova resolveu, nem regride LastSeenAt.
                var current = await PublishAsync(gate, connectorId, now, async batchCt =>
                {
                    var existingInstalls = await _db.SoftwareInstallations
                        .Where(si => si.ConnectorConfigId == connectorId && si.AssetId == assetId && productIdsInBatch.Contains(si.SoftwareProductId))
                        .ToListAsync(batchCt);
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

                    await _db.SaveChangesAsync(batchCt);
                }, ct);
                _db.ChangeTracker.Clear();
                if (!current) return Stop();
                await HitAsync(CheckpointBatchCommitted, ct);
            }
        }

        // (3) FAIL-CLOSED: resolução/desativação por omissão só em coleta COMPLETA (Available) — nunca em Partial. Com a
        // marca, seleção e UPDATE acontecem na MESMA transação, sob a trava da fonte, e só se esta aquisição ainda for a
        // mais recente: toda instalação que não carrega ESTA marca foi vista só por aquisições anteriores (uma mais nova
        // teria movido a marca da fonte) — nunca a presença de uma aquisição mais recente.
        if (incoming.State == SoftwareInventoryCollectionState.Available)
        {
            await HitAsync(CheckpointBeforeAbsence, ct);
            var current = await PublishAsync(gate, connectorId, now, async absenceCt =>
            {
                await HitAsync(CheckpointAbsenceLocked, absenceCt);
                installationsResolved = await _db.SoftwareInstallations
                    .Where(si => si.ConnectorConfigId == connectorId && si.LifecycleState != ObservationLifecycle.Resolved && si.LastSeenAt != now)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(si => si.LifecycleState, ObservationLifecycle.Resolved)
                        .SetProperty(si => si.ResolvedAt, now), absenceCt);

                var staleBindingIds = await _db.SoftwareProductSourceBindings
                    .Where(b => b.ConnectorConfigId == connectorId && b.IsActive && !seenExternalIds.Contains(b.ExternalProductId))
                    .Select(b => b.Id)
                    .ToListAsync(absenceCt);
                if (staleBindingIds.Count > 0)
                {
                    bindingsDeactivated = await _db.SoftwareProductSourceBindings
                        .Where(b => staleBindingIds.Contains(b.Id))
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(b => b.IsActive, false)
                            .SetProperty(b => b.ResolvedAt, now), absenceCt);
                }
            }, ct);
            if (!current) return Stop();
        }
        _db.ChangeTracker.Clear();

        // (4) Recompute agregado dos produtos TOCADOS (produtos criados/atualizados nesta coleta + os desativados
        // acima), a partir dos bindings ATIVOS de TODAS as fontes — mesmo idioma do RecomputeAssetsBatchedAsync.
        var touchedProductIds = new HashSet<Guid>(naturalKeyToProductId.Values);
        if (!await RecomputeProductsBatchedAsync(gate, connectorId, now, touchedProductIds, ct)) return Stop();

        // (5) Snapshot agregado do conector: cache de KPIs + estado/última tentativa/última coleta — só pela aquisição
        // que ainda é a mais recente.
        if (!await PublishAsync(gate, connectorId, now, c => UpsertSnapshotAsync(connectorId, incoming, now, c), ct))
            return Stop();

        if (orphanInstallations > 0)
            _log?.LogInformation(
                "Inventário de software do conector {ConnectorId}: {Orphans} instalação(ões) órfã(s) (produto/máquina fora da fotografia) ignorada(s).",
                connectorId, orphanInstallations);

        return Result();
    }

    /// <summary>
    /// Publica UM passo desta dimensão. Sem marca de aquisição (<paramref name="gate"/> nulo), executa direto, como
    /// antes. Com ela, delega à coordenação da fonte já usada por dispositivos e vulnerabilidades
    /// (<see cref="DeviceIdentityResolver.RunIfCurrentAsync"/>): transação, trava da fonte e marca ainda publicada — senão
    /// devolve <c>false</c> sem executar. A trava vale só pela duração do passo; nenhuma chamada HTTP acontece aqui.
    /// </summary>
    private static async Task<bool> PublishAsync(
        DeviceIdentityResolver? gate, Guid connectorId, DateTimeOffset marker,
        Func<CancellationToken, Task> work, CancellationToken ct)
    {
        if (gate is null)
        {
            await work(ct);
            return true;
        }
        return await gate.RunIfCurrentAsync(connectorId, marker, work, ct);
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

    /// <summary>Devolve <c>false</c> se a aquisição foi superada no meio do recálculo (os lotes seguintes não rodam).</summary>
    private async Task<bool> RecomputeProductsBatchedAsync(
        DeviceIdentityResolver? gate, Guid connectorId, DateTimeOffset marker, HashSet<Guid> productIds, CancellationToken ct)
    {
        if (productIds.Count == 0) return true;

        foreach (var idBatch in productIds.Chunk(ProductBatchSize))
        {
            var current = await PublishAsync(gate, connectorId, marker, async batchCt =>
            {
                var products = await _db.SoftwareProducts.Where(p => idBatch.Contains(p.Id)).ToListAsync(batchCt);
                var activeBindings = await _db.SoftwareProductSourceBindings.AsNoTracking()
                    .Where(b => idBatch.Contains(b.SoftwareProductId) && b.IsActive)
                    .ToListAsync(batchCt);
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

                await _db.SaveChangesAsync(batchCt);
            }, ct);
            _db.ChangeTracker.Clear();
            if (!current) return false;
        }
        return true;
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

    private Task HitAsync(string checkpoint, CancellationToken ct) =>
        Checkpoint is null ? Task.CompletedTask : Checkpoint(checkpoint, ct);

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
