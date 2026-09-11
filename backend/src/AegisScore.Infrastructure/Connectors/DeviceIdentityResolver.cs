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
/// [AEGIS-ENTITY-RESOLUTION-01] AUTORIDADE ÚNICA de resolução de dispositivos entre fontes. É o único ponto que
/// cria/reutiliza <see cref="Asset"/> e <see cref="AssetSourceBinding"/> a partir de observações de dispositivos —
/// usado pelas máquinas do Defender (<see cref="VulnerabilityReconciler"/>) e pelos dispositivos gerenciados do
/// Intune (executor de ingestão). Nenhum reconciliador decide sozinho qual é o ativo de um dispositivo.
///
/// Regras (e só estas):
///   • binding EXISTENTE nunca muda de ativo — o identificador canônico é preservado em reexecuções;
///   • com chave forte VÁLIDA (tenant + diretório confirmado + identificador de dispositivo do Entra) e sem
///     conflito: reutiliza o ativo da chave, mantendo um binding distinto por fonte — nas duas ordens de chegada;
///   • sem chave forte (ausente, inválida, diretório não confirmado): preserva o objeto como observação da fonte,
///     em ativo próprio, e declara "ainda não vinculado" — nunca "dispositivo diferente";
///   • contradição (identificador mudou; chave já pertence a outro ativo; ativo já tem outro dispositivo): nada
///     é escolhido arbitrariamente, nada é movido, fundido ou apagado — o binding é marcado em conflito com o
///     motivo e as referências;
///   • nome, hostname, IP e semelhança textual NUNCA participam da decisão.
///
/// Atomicidade e concorrência: a decisão acontece numa TRANSAÇÃO; no PostgreSQL, sob uma trava consultiva de
/// <c>(tenant, diretório)</c> (<see cref="DeviceDirectoryLock"/>) — Defender e Intune do mesmo diretório se
/// serializam em vez de disputar, e as releituras dentro da trava enxergam o que o outro acabou de gravar. Os
/// índices únicos nomeados continuam sendo a garantia final: uma violação DELES (e só deles) desfaz a passada e
/// é reaplicada uma vez; qualquer outro erro sobe.
///
/// Ordem determinística: observações com binding existente primeiro (a história estabelecida detém a chave
/// antes de um registro novo), depois as novas; dentro de cada grupo, pelo id na fonte (ordinal).
/// </summary>
public sealed class DeviceIdentityResolver
{
    private const string BindingNaturalIndex = "UX_AssetSourceBinding_Natural";
    private const string KeyNaturalIndex = "UX_AssetStrongIdentifier_Natural";
    private const string KeyAssetScopeIndex = "UX_AssetStrongIdentifier_AssetScope";

    private const int LookupChunk = 1_000;
    private const int AssetBatchSize = 500;
    private const int MaxNameLength = 200;
    private const int MaxSubTypeLength = 100;
    private const int MaxSourceLabelLength = 200;

    /// <summary>Rótulo provisório quando a fonte não coleta nome — nunca um identificador técnico.</summary>
    internal const string PlaceholderNamePrefix = "Dispositivo sem nome coletado";

    private readonly AegisScoreDbContext _db;
    private readonly ILogger? _log;

    public DeviceIdentityResolver(AegisScoreDbContext db, ILogger? log = null)
    {
        _db = db;
        _log = log;
    }

    /// <summary>Resultado de uma passada: ativo de cada id na fonte, ativos observados e contagens.</summary>
    public sealed record ResolutionOutcome(
        IReadOnlyDictionary<string, Guid> AssetByExternalId,
        IReadOnlySet<Guid> ObservedAssetIds,
        DeviceResolutionSyncResult Counts);

    /// <summary>
    /// Resolve as observações de UMA fonte (conector) numa passada atômica. <paramref name="now"/> é o marcador
    /// da fotografia atual: cada binding observado recebe <c>LastObservedAt = now</c>, e é por ele que uma
    /// desativação posterior (só em coleta completa) reconhece quem ficou de fora.
    /// </summary>
    public async Task<ResolutionOutcome> ResolveAsync(
        Guid connectorId, string sourceLabel, string? directoryNamespace,
        IReadOnlyList<DeviceSourceObservation> observations, DateTimeOffset now, CancellationToken ct)
    {
        // Normalização defensiva: quem chama já normalizou, mas a autoridade não confia no valor recebido.
        var ns = DeviceDirectoryIdentifiers.NormalizeNamespace(directoryNamespace);
        var unique = observations
            .Where(o => !string.IsNullOrWhiteSpace(o.ExternalId))
            .GroupBy(o => o.ExternalId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await ResolveAttemptAsync(connectorId, sourceLabel, ns, unique, now, ct);
            }
            catch (DbUpdateException ex) when (attempt == 0 && IsKnownRace(ex))
            {
                _db.ChangeTracker.Clear();
                _log?.LogInformation(
                    "Corrida conhecida na resolução de dispositivos do conector {ConnectorId} — relendo e reaplicando a passada.",
                    connectorId);
            }
        }

        throw new InvalidOperationException("Resolução de dispositivos falhou após reaplicar a corrida conhecida.");
    }

    /// <summary>
    /// Passada completa de uma fonte de GESTÃO de dispositivos (fotografia por dispositivo): resolve, desativa os
    /// bindings AUSENTES somente quando a dimensão da fonte é comprovadamente completa, e recalcula o agregado dos
    /// ativos tocados considerando os bindings ativos de TODAS as fontes.
    /// </summary>
    public async Task<DeviceResolutionSyncResult> ReconcileSnapshotAsync(
        Guid connectorId, string sourceLabel, string? directoryNamespace,
        IReadOnlyList<DeviceSourceObservation> observations, bool completeSnapshot,
        DateTimeOffset now, CancellationToken ct)
    {
        var outcome = await ResolveAsync(connectorId, sourceLabel, directoryNamespace, observations, now, ct);
        _db.ChangeTracker.Clear();

        var touched = new HashSet<Guid>(outcome.ObservedAssetIds);
        var deactivated = 0;
        if (completeSnapshot)
        {
            var (count, assetIds) = await DeactivateMissingAsync(connectorId, now, ct);
            deactivated = count;
            foreach (var id in assetIds) touched.Add(id);
        }

        await RecomputeAssetsAsync(touched, ct);
        return outcome.Counts with { BindingsDeactivated = deactivated, DeactivationApplied = completeSnapshot };
    }

    /// <summary>
    /// Desativa SÓ os bindings DESTA fonte que não foram observados na fotografia <paramref name="now"/>. Chamado
    /// exclusivamente depois de uma coleta completa da dimensão da própria fonte — nunca por status geral do
    /// conector. Não toca bindings de outras fontes nem a chave forte (ausência não é exclusão).
    /// </summary>
    public async Task<(int Deactivated, IReadOnlySet<Guid> AssetIds)> DeactivateMissingAsync(
        Guid connectorId, DateTimeOffset now, CancellationToken ct)
    {
        var stale = await _db.AssetSourceBindings
            .AsNoTracking()
            .Where(b => b.ConnectorConfigId == connectorId && b.IsActive && b.LastObservedAt != now)
            .Select(b => new { b.Id, b.AssetId })
            .ToListAsync(ct);
        var assetIds = stale.Select(s => s.AssetId).ToHashSet();
        if (stale.Count == 0) return (0, assetIds);

        var deactivated = 0;
        foreach (var chunk in stale.Select(s => s.Id).Chunk(LookupChunk))
        {
            var ids = chunk.ToList();
            deactivated += await _db.AssetSourceBindings
                .Where(b => ids.Contains(b.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.IsActive, false)
                    .SetProperty(b => b.ResolvedAt, now), ct);
        }
        return (deactivated, assetIds);
    }

    /// <summary>
    /// Recalcula o agregado dos ativos DESCOBERTOS por conector a partir dos bindings ativos de TODAS as fontes: o
    /// ativo fica ativo enquanto QUALQUER fonte o observar. Ativos manuais/CMDB não são tocados. Devolve quantos
    /// ativos passaram de ativo para inativo.
    /// </summary>
    public async Task<int> RecomputeAssetsAsync(IEnumerable<Guid> assetIds, CancellationToken ct)
    {
        var deactivated = 0;
        foreach (var ids in assetIds.Distinct().Chunk(AssetBatchSize))
        {
            var idList = ids.ToList();
            var assets = await _db.Assets.Where(a => idList.Contains(a.Id)).ToListAsync(ct);
            var activeBindings = await _db.AssetSourceBindings
                .AsNoTracking()
                .Where(b => idList.Contains(b.AssetId) && b.IsActive)
                .Select(b => new { b.AssetId, b.SourceLastSeenAt, b.LastObservedAt })
                .ToListAsync(ct);
            var byAsset = activeBindings.GroupBy(b => b.AssetId).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var asset in assets)
            {
                if (asset.DiscoverySource != AssetDiscoverySource.Connector) continue;
                var hasActive = byAsset.TryGetValue(asset.Id, out var bs) && bs.Count > 0;
                if (asset.IsActive && !hasActive) deactivated++;
                asset.IsActive = hasActive;
                if (hasActive)
                {
                    var lastSeen = bs!.Max(b => b.SourceLastSeenAt ?? b.LastObservedAt);
                    if (asset.LastSeenAt is null || lastSeen > asset.LastSeenAt) asset.LastSeenAt = lastSeen;
                }
            }

            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
        }
        return deactivated;
    }

    // ---- Passada ------------------------------------------------------------------------------------------------

    private async Task<ResolutionOutcome> ResolveAttemptAsync(
        Guid connectorId, string sourceLabel, string? ns,
        List<DeviceSourceObservation> observations, DateTimeOffset now, CancellationToken ct)
    {
        // O conector é lido sob o query filter do contexto: um conector de outro tenant simplesmente não existe
        // aqui (fail-closed), e o tenant da trava vem do registro, não de quem chama.
        var tenantId = await _db.Connectors.AsNoTracking()
            .Where(c => c.Id == connectorId)
            .Select(c => (Guid?)c.TenantId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                "Conector da resolução de dispositivos não encontrado no tenant do contexto (fail-closed).");

        var label = TrimTo(sourceLabel, MaxSourceLabelLength) ?? "Fonte integrada";

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        if (ns is not null && _db.Database.IsNpgsql())
        {
            // Trava consultiva de TRANSAÇÃO (liberada no commit/rollback): a seção ler → decidir → gravar das chaves
            // deste diretório é exclusiva. Sem namespace não há chave forte a criar, e não há o que serializar
            // além do que o índice natural do binding já garante. SQLite serializa escritores na própria conexão.
            await _db.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock({0})",
                new object[] { DeviceDirectoryLock.AdvisoryKey(tenantId, ns) }, ct);
        }

        // (1) Bindings desta fonte. Rastreados: são atualizados nesta passada.
        var bindings = await _db.AssetSourceBindings
            .Where(b => b.ConnectorConfigId == connectorId)
            .ToListAsync(ct);
        var bindingByExternalId = bindings
            .GroupBy(b => b.ExternalId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // (2) Chaves fortes relevantes — lidas DENTRO da trava, depois de qualquer gravação concorrente do diretório.
        var keyByValue = new Dictionary<string, AssetStrongIdentifier>(StringComparer.Ordinal);
        var keyByAsset = new Dictionary<Guid, AssetStrongIdentifier>();
        if (ns is not null)
        {
            var values = observations
                .Where(o => o.DirectoryDeviceId is { IsValid: true })
                .Select(o => o.DirectoryDeviceId.Value!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            foreach (var chunk in values.Chunk(LookupChunk))
            {
                var list = chunk.ToList();
                var rows = await _db.AssetStrongIdentifiers.AsNoTracking()
                    .Where(k => k.DirectoryNamespace == ns
                        && k.IdentifierType == AssetStrongIdentifierType.EntraDeviceId
                        && list.Contains(k.IdentifierValue))
                    .ToListAsync(ct);
                foreach (var k in rows) keyByValue[k.IdentifierValue] = k;
            }

            var observedAssetIdsOfExisting = observations
                .Where(o => bindingByExternalId.ContainsKey(o.ExternalId))
                .Select(o => bindingByExternalId[o.ExternalId].AssetId)
                .Distinct()
                .ToList();
            foreach (var chunk in observedAssetIdsOfExisting.Chunk(LookupChunk))
            {
                var list = chunk.ToList();
                var rows = await _db.AssetStrongIdentifiers.AsNoTracking()
                    .Where(k => k.DirectoryNamespace == ns
                        && k.IdentifierType == AssetStrongIdentifierType.EntraDeviceId
                        && list.Contains(k.AssetId))
                    .ToListAsync(ct);
                foreach (var k in rows) keyByAsset[k.AssetId] = k;
            }
            foreach (var k in keyByValue.Values) keyByAsset.TryAdd(k.AssetId, k);
        }

        // (3) Decisão, em ordem determinística: história estabelecida primeiro, depois registros novos.
        var ordered = observations
            .OrderBy(o => bindingByExternalId.ContainsKey(o.ExternalId) ? 0 : 1)
            .ThenBy(o => o.ExternalId, StringComparer.Ordinal)
            .ToList();

        var assetByExternalId = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var observedAssetIds = new HashSet<Guid>();
        var createdAssetIds = new HashSet<Guid>();
        var nameCandidates = new Dictionary<Guid, string>();
        var tally = new Tally();

        foreach (var o in ordered)
        {
            var idObs = o.DirectoryDeviceId ?? DirectoryDeviceIdObservation.NotProvided;
            var key = ns is not null && idObs.IsValid ? idObs.Value : null;
            var evidenceState = key is not null
                ? AssetBindingResolutionState.Linked
                : idObs.Status switch
                {
                    DirectoryIdentifierStatus.Provided => AssetBindingResolutionState.DirectoryUnconfirmed,
                    DirectoryIdentifierStatus.Invalid => AssetBindingResolutionState.InvalidIdentifier,
                    _ => AssetBindingResolutionState.NoIdentifier,
                };

            AssetSourceBinding binding;
            if (bindingByExternalId.TryGetValue(o.ExternalId, out var existing))
            {
                binding = existing;
                ApplyObservation(binding, o, idObs, label, now);
                EvaluateExisting(binding, ns, key, evidenceState, keyByValue, keyByAsset, connectorId, label, now, tally);
            }
            else
            {
                Guid assetId;
                var linked = false;
                if (key is not null && keyByValue.TryGetValue(key, out var held))
                {
                    assetId = held.AssetId;   // outra fonte (ou esta, por outro registro) já estabeleceu o dispositivo
                    linked = true;
                }
                else
                {
                    var asset = NewAsset(o, label, now);
                    _db.Assets.Add(asset);
                    createdAssetIds.Add(asset.Id);
                    tally.AssetsCreated++;
                    assetId = asset.Id;
                    if (key is not null)
                    {
                        EstablishKey(ns!, key, assetId, connectorId, label, now, keyByValue, keyByAsset);
                        tally.KeysEstablished++;
                        linked = true;
                    }
                }

                binding = new AssetSourceBinding
                {
                    AssetId = assetId,
                    ConnectorConfigId = connectorId,
                    ExternalId = o.ExternalId,
                    FirstObservedAt = now,
                };
                ApplyObservation(binding, o, idObs, label, now);
                if (linked) SetLinked(binding, ns!, key!, now);
                else SetEvidenceState(binding, ns, evidenceState);
                _db.AssetSourceBindings.Add(binding);
                bindingByExternalId[o.ExternalId] = binding;
                tally.BindingsCreated++;
            }

            tally.Count(binding.ResolutionState);
            assetByExternalId[o.ExternalId] = binding.AssetId;
            observedAssetIds.Add(binding.AssetId);

            var observedName = TrimTo(o.DisplayName, MaxNameLength);
            if (observedName is not null && !createdAssetIds.Contains(binding.AssetId))
                nameCandidates.TryAdd(binding.AssetId, observedName);
        }

        // (4) Nome PROVISÓRIO substituído pelo primeiro nome observado — e somente ele. Nome curado, manual ou
        // legado (NameOrigin.Unspecified) nunca é tocado; responsável, criticidade e relações também não.
        foreach (var chunk in nameCandidates.Keys.Chunk(LookupChunk))
        {
            var list = chunk.ToList();
            var placeholders = await _db.Assets
                .Where(a => list.Contains(a.Id) && a.NameOrigin == AssetNameOrigin.Placeholder)
                .ToListAsync(ct);
            foreach (var a in placeholders)
            {
                a.Name = nameCandidates[a.Id];
                a.NameOrigin = AssetNameOrigin.ObservedBySource;
            }
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (tally.Conflicts > 0)
            _log?.LogWarning(
                "Resolução de dispositivos do conector {ConnectorId}: {Conflicts} conflito(s) preservado(s) para análise (nenhum ativo escolhido arbitrariamente).",
                connectorId, tally.Conflicts);
        _log?.LogInformation(
            "Resolução de dispositivos do conector {ConnectorId}: {Observed} observado(s), {Linked} vinculado(s) por identificador de diretório, {Created} ativo(s) criado(s), {Keys} chave(s) estabelecida(s), {NoId} sem identificador, {Invalid} com identificador inválido, {Unconfirmed} com diretório não confirmado.",
            connectorId, observations.Count, tally.Linked, tally.AssetsCreated, tally.KeysEstablished,
            tally.WithoutIdentifier, tally.InvalidIdentifier, tally.DirectoryUnconfirmed);

        return new ResolutionOutcome(
            assetByExternalId,
            observedAssetIds,
            new DeviceResolutionSyncResult(
                Observed: observations.Count,
                AssetsCreated: tally.AssetsCreated,
                BindingsCreated: tally.BindingsCreated,
                Linked: tally.Linked,
                KeysEstablished: tally.KeysEstablished,
                WithoutIdentifier: tally.WithoutIdentifier,
                InvalidIdentifier: tally.InvalidIdentifier,
                DirectoryUnconfirmed: tally.DirectoryUnconfirmed,
                Conflicts: tally.Conflicts,
                BindingsDeactivated: 0,
                DeactivationApplied: false));
    }

    /// <summary>
    /// Reavalia um binding EXISTENTE. Ele nunca muda de ativo; o que muda é o que se pode afirmar sobre o vínculo.
    /// </summary>
    private void EvaluateExisting(
        AssetSourceBinding b, string? ns, string? key, AssetBindingResolutionState evidenceState,
        Dictionary<string, AssetStrongIdentifier> keyByValue, Dictionary<Guid, AssetStrongIdentifier> keyByAsset,
        Guid connectorId, string label, DateTimeOffset now, Tally tally)
    {
        // "Estabelecido" = este binding já foi vinculado por um identificador (e continua respondendo por ele,
        // mesmo quando uma observação posterior o contradisse).
        var established = b.DirectoryDeviceId is not null && b.DirectoryNamespace is not null
            && (b.ResolutionState == AssetBindingResolutionState.Linked
                || (b.ResolutionState == AssetBindingResolutionState.Conflict
                    && b.ConflictKind == AssetBindingConflictKind.IdentifierChanged));

        if (key is null)
        {
            // Sem evidência forte NESTA observação. Um vínculo estabelecido antes não é desfeito pela ausência
            // (o status da última observação fica registrado à parte); sem vínculo anterior, declara-se a falta.
            if (established) return;
            SetEvidenceState(b, ns, evidenceState);
            return;
        }

        if (established)
        {
            if (!string.Equals(b.DirectoryNamespace, ns, StringComparison.Ordinal)
                || !string.Equals(b.DirectoryDeviceId, key, StringComparison.Ordinal))
            {
                // CONTRADIÇÃO: a fonte passou a informar outro dispositivo (ou outro diretório) para o mesmo
                // registro. O binding NÃO é movido; o vínculo estabelecido fica, a observação nova vira referência.
                SetConflict(b, AssetBindingConflictKind.IdentifierChanged, key,
                    keyByValue.TryGetValue(key, out var other) && other.AssetId != b.AssetId ? other.AssetId : null);
                return;
            }
            // Mesmo identificador de sempre: segue para confirmar a chave (e encerrar um conflito anterior).
        }

        if (keyByValue.TryGetValue(key, out var held))
        {
            if (held.AssetId == b.AssetId) SetLinked(b, ns!, key, now);
            else SetConflict(b, AssetBindingConflictKind.IdentifierHeldByOtherAsset, key, held.AssetId);
            return;
        }

        if (keyByAsset.TryGetValue(b.AssetId, out var assetKey)
            && !string.Equals(assetKey.IdentifierValue, key, StringComparison.Ordinal))
        {
            SetConflict(b, AssetBindingConflictKind.AssetHeldByOtherIdentifier, key, null);
            return;
        }

        EstablishKey(ns!, key, b.AssetId, connectorId, label, now, keyByValue, keyByAsset);
        tally.KeysEstablished++;
        SetLinked(b, ns!, key, now);
    }

    private void EstablishKey(
        string ns, string key, Guid assetId, Guid connectorId, string label, DateTimeOffset now,
        Dictionary<string, AssetStrongIdentifier> keyByValue, Dictionary<Guid, AssetStrongIdentifier> keyByAsset)
    {
        var k = new AssetStrongIdentifier
        {
            AssetId = assetId,
            DirectoryNamespace = ns,
            IdentifierType = AssetStrongIdentifierType.EntraDeviceId,
            IdentifierValue = key,
            EstablishedAt = now,
            EstablishedByConnectorConfigId = connectorId,
            EstablishedBySource = label,
        };
        _db.AssetStrongIdentifiers.Add(k);
        keyByValue[key] = k;
        keyByAsset[assetId] = k;
    }

    private static Asset NewAsset(DeviceSourceObservation o, string label, DateTimeOffset now)
    {
        var observedName = TrimTo(o.DisplayName, MaxNameLength);
        var subType = TrimTo(o.SubType, MaxSubTypeLength);
        return new Asset
        {
            Name = observedName ?? PlaceholderName(label, subType),
            NameOrigin = observedName is not null ? AssetNameOrigin.ObservedBySource : AssetNameOrigin.Placeholder,
            Category = AssetCategory.Hardware,
            SubType = subType,
            DiscoverySource = AssetDiscoverySource.Connector,
            // Valor padrão do modelo — NÃO é inferência da ferramenta; a resolução nunca o altera depois.
            Criticality = 1,
            IsActive = true,
            LastSeenAt = o.SourceLastSeenAt ?? now,
        };
    }

    /// <summary>Rótulo provisório legível — nunca o id técnico da fonte, que fica restrito ao diagnóstico.</summary>
    internal static string PlaceholderName(string sourceLabel, string? platform)
    {
        var name = platform is null
            ? $"{PlaceholderNamePrefix} · {sourceLabel}"
            : $"{PlaceholderNamePrefix} · {platform} · {sourceLabel}";
        return name.Length <= MaxNameLength ? name : name[..MaxNameLength];
    }

    private static void ApplyObservation(
        AssetSourceBinding b, DeviceSourceObservation o, DirectoryDeviceIdObservation idObs, string label, DateTimeOffset now)
    {
        b.DisplayName = TrimTo(o.DisplayName, MaxNameLength);
        b.SubType = TrimTo(o.SubType, MaxSubTypeLength);
        b.LastObservedAt = now;
        b.SourceLastSeenAt = o.SourceLastSeenAt;
        b.IsActive = true;
        b.ResolvedAt = null;
        b.SourceLabel = label;
        b.SourceCompliance = o.Compliance;
        b.SourceEncryption = o.Encryption;
        b.DirectoryIdStatus = idObs.Status;
        b.ResolutionEvaluatedAt = now;
    }

    private static void SetLinked(AssetSourceBinding b, string ns, string key, DateTimeOffset now)
    {
        b.ResolutionState = AssetBindingResolutionState.Linked;
        b.DirectoryNamespace = ns;
        b.DirectoryDeviceId = key;
        b.ConflictKind = AssetBindingConflictKind.None;
        b.ConflictDirectoryDeviceId = null;
        b.ConflictAssetId = null;
        b.LinkedAt ??= now;
    }

    private static void SetEvidenceState(AssetSourceBinding b, string? ns, AssetBindingResolutionState state)
    {
        b.ResolutionState = state;
        b.DirectoryNamespace = ns;
        b.ConflictKind = AssetBindingConflictKind.None;
        b.ConflictDirectoryDeviceId = null;
        b.ConflictAssetId = null;
    }

    private static void SetConflict(
        AssetSourceBinding b, AssetBindingConflictKind kind, string observedKey, Guid? otherAssetId)
    {
        b.ResolutionState = AssetBindingResolutionState.Conflict;
        b.ConflictKind = kind;
        b.ConflictDirectoryDeviceId = observedKey;
        b.ConflictAssetId = otherAssetId;
    }

    private sealed class Tally
    {
        public int AssetsCreated, BindingsCreated, KeysEstablished;
        public int Linked, WithoutIdentifier, InvalidIdentifier, DirectoryUnconfirmed, Conflicts;

        public void Count(AssetBindingResolutionState state)
        {
            switch (state)
            {
                case AssetBindingResolutionState.Linked: Linked++; break;
                case AssetBindingResolutionState.NoIdentifier: WithoutIdentifier++; break;
                case AssetBindingResolutionState.InvalidIdentifier: InvalidIdentifier++; break;
                case AssetBindingResolutionState.DirectoryUnconfirmed: DirectoryUnconfirmed++; break;
                case AssetBindingResolutionState.Conflict: Conflicts++; break;
            }
        }
    }

    private static string? TrimTo(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        return t.Length <= max ? t : t[..max];
    }

    /// <summary>
    /// SOMENTE as violações dos índices únicos nomeados desta resolução são corrida recuperável. Qualquer outro
    /// erro (FK, CHECK, deadlock, timeout…) é falha real e sobe.
    /// </summary>
    internal static bool IsKnownRace(DbUpdateException ex)
    {
        if (ex.InnerException is PostgresException pg)
            return pg.SqlState == PostgresErrorCodes.UniqueViolation
                && pg.ConstraintName is BindingNaturalIndex or KeyNaturalIndex or KeyAssetScopeIndex;

        var inner = ex.InnerException;
        return inner is not null
            && inner.GetType().Name == "SqliteException"
            && inner.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
            && (inner.Message.Contains("AssetSourceBindings", StringComparison.OrdinalIgnoreCase)
                || inner.Message.Contains("AssetStrongIdentifiers", StringComparison.OrdinalIgnoreCase));
    }
}
