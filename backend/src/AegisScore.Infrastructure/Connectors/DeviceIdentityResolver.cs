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
///   • contradição (identificador ou diretório mudou; chave já pertence a outro ativo; ativo já tem outro
///     dispositivo; identificadores divergentes para o mesmo registro na MESMA coleta): nada é escolhido
///     arbitrariamente, nada é movido, fundido ou apagado — o binding é marcado em conflito com o motivo e o par
///     diretório/identificador observado, separado do vínculo estabelecido;
///   • nome, hostname, IP e semelhança textual NUNCA participam da decisão.
///
/// Repetições do mesmo registro numa coleta são combinadas pela regra única
/// (<see cref="DeviceSourceObservations.Consolidate"/>): o resultado não depende da ordem das páginas. Um id na fonte
/// fora do contrato é recusado (nunca truncado) e torna a passada incompleta.
///
/// CICLO DE VIDA E PRECEDÊNCIA. Cada passada carrega a MARCA da sua fotografia (instante em que a coleta começou).
/// Presença, ausência e a marca da fonte (<see cref="ConnectorConfig.DeviceSnapshotWatermark"/>) são publicadas sob
/// uma trava consultiva de transação da FONTE (<see cref="DeviceSourceLock"/>), no banco — vale para várias instâncias:
///   • presença: se a marca da passada é ANTERIOR à da fonte, a passada foi superada e não publica nada; senão ela
///     passa a ser a marca da fonte, na mesma transação da presença;
///   • ausência: só a passada cuja marca AINDA é a da fonte desativa — seleção e UPDATE por PREDICADO acontecem na
///     mesma transação, sob a mesma trava, então nenhuma presença intercala entre elas; uma passada mais nova que já
///     publicou impede a ausência da anterior (que, portanto, nunca desativa o que a mais nova observou);
///   • estado consolidado do ativo: recalculado depois da mudança dos bindings, com os ativos travados por linha
///     (<c>FOR NO KEY UPDATE</c>, em ordem) e os bindings de TODAS as fontes relidos DEPOIS da trava — o último
///     recálculo sempre enxerga a última mudança confirmada, qualquer que seja a fonte.
/// Nenhuma transação fica aberta durante chamada HTTP: a coleta já terminou quando a resolução começa.
///
/// Concorrência entre fontes: a decisão das chaves acontece sob a trava do <c>(tenant, diretório)</c>
/// (<see cref="DeviceDirectoryLock"/>), adquirida DEPOIS da trava da fonte (ordem fixa). Os índices únicos nomeados
/// continuam sendo a garantia final: uma violação DELES (e só deles) desfaz a passada e é reaplicada uma vez.
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
    private const int MaxConflictValues = 5;

    // Pontos de observação para testes de intercalação (barreiras). Sem efeito quando Checkpoint é nulo.
    internal const string CheckpointPresenceCommitted = "presence-committed";
    internal const string CheckpointAbsenceLocked = "absence-locked";
    internal const string CheckpointRecomputeLocked = "recompute-locked";

    /// <summary>Rótulo provisório quando a fonte não coleta nome — nunca um identificador técnico.</summary>
    internal const string PlaceholderNamePrefix = "Dispositivo sem nome coletado";

    private readonly AegisScoreDbContext _db;
    private readonly ILogger? _log;
    private readonly Dictionary<Guid, Guid> _tenantByConnector = new();

    public DeviceIdentityResolver(AegisScoreDbContext db, ILogger? log = null)
    {
        _db = db;
        _log = log;
    }

    /// <summary>SOMENTE para testes: barreira chamada nos pontos <c>Checkpoint*</c> (dentro das travas, quando há).</summary>
    internal Func<string, CancellationToken, Task>? Checkpoint { get; init; }

    /// <summary>Resultado de uma passada: ativo de cada id na fonte, ativos observados, contagens e a marca usada.</summary>
    public sealed record ResolutionOutcome(
        IReadOnlyDictionary<string, Guid> AssetByExternalId,
        IReadOnlySet<Guid> ObservedAssetIds,
        DeviceResolutionSyncResult Counts,
        DateTimeOffset Marker)
    {
        /// <summary>Uma fotografia mais recente desta fonte já tinha sido publicada: nada foi escrito.</summary>
        public bool Superseded => Counts.Superseded;
    }

    /// <summary>Resultado da publicação da ausência de uma fonte.</summary>
    public sealed record AbsenceOutcome(
        bool Applied, bool Superseded, int BindingsDeactivated, IReadOnlySet<Guid> AssetIds, int CompanionCount);

    /// <summary>
    /// Resolve as observações de UMA fonte (conector) numa passada atômica. <paramref name="snapshotAt"/> é a marca
    /// da fotografia (instante em que a coleta começou): cada binding observado recebe <c>LastObservedAt</c> = marca,
    /// e é por ela que a ausência posterior (só em coleta completa, e só se a marca ainda for a da fonte) reconhece
    /// quem ficou de fora.
    /// </summary>
    public async Task<ResolutionOutcome> ResolveAsync(
        Guid connectorId, string sourceLabel, string? directoryNamespace,
        IReadOnlyList<DeviceSourceObservation> observations, DateTimeOffset snapshotAt, CancellationToken ct)
    {
        var marker = DeviceSnapshotMarker.Normalize(snapshotAt);
        // Normalização defensiva: quem chama já normalizou, mas a autoridade não confia no valor recebido.
        var ns = DeviceDirectoryIdentifiers.NormalizeNamespace(directoryNamespace);
        var accepted = observations
            .Where(o => DeviceSourceObservations.IsExternalIdWithinContract(o.ExternalId))
            .ToList();
        var rejected = observations.Count - accepted.Count;
        var unique = DeviceSourceObservations.Consolidate(accepted);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var outcome = await ResolveAttemptAsync(
                    connectorId, sourceLabel, ns, unique, observations.Count, rejected, marker, ct);
                await HitAsync(CheckpointPresenceCommitted, ct);
                return outcome;
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
    /// Passada completa de uma fonte de GESTÃO de dispositivos (fotografia por dispositivo): resolve, publica a
    /// ausência somente quando a dimensão da fonte é comprovadamente completa (e a passada não foi superada), e
    /// recalcula o agregado dos ativos tocados considerando os bindings ativos de TODAS as fontes.
    /// </summary>
    public async Task<DeviceResolutionSyncResult> ReconcileSnapshotAsync(
        Guid connectorId, string sourceLabel, string? directoryNamespace,
        IReadOnlyList<DeviceSourceObservation> observations, bool completeSnapshot,
        DateTimeOffset snapshotAt, CancellationToken ct)
    {
        var outcome = await ResolveAsync(connectorId, sourceLabel, directoryNamespace, observations, snapshotAt, ct);
        _db.ChangeTracker.Clear();
        if (outcome.Superseded) return outcome.Counts;

        var touched = new HashSet<Guid>(outcome.ObservedAssetIds);
        var counts = outcome.Counts;
        if (completeSnapshot && counts.RejectedObservations == 0)
        {
            var absence = await PublishAbsenceAsync(connectorId, outcome.Marker, companion: null, ct);
            foreach (var id in absence.AssetIds) touched.Add(id);
            counts = counts with
            {
                BindingsDeactivated = absence.BindingsDeactivated,
                DeactivationApplied = absence.Applied,
                Superseded = absence.Superseded,
            };
        }

        await RecomputeAssetsAsync(touched, ct);
        return counts;
    }

    /// <summary>
    /// Publica a AUSÊNCIA desta fonte na fotografia <paramref name="snapshotAt"/>: desativa SÓ os bindings DESTA fonte
    /// não observados nela. Chamado exclusivamente depois de uma coleta completa da dimensão da própria fonte — nunca
    /// por status geral do conector. Sob a trava da fonte e somente se a marca ainda for a da fonte: seleção e UPDATE
    /// usam o MESMO predicado, na MESMA transação (nenhuma presença intercala). <paramref name="companion"/> executa,
    /// na mesma transação e sob a mesma condição, a ausência de fatos da fonte ligados a esta fotografia (ex.:
    /// observações de vulnerabilidade do Defender). Não toca bindings de outras fontes nem a chave forte.
    /// </summary>
    public async Task<AbsenceOutcome> PublishAbsenceAsync(
        Guid connectorId, DateTimeOffset snapshotAt, Func<CancellationToken, Task<int>>? companion, CancellationToken ct)
    {
        var marker = DeviceSnapshotMarker.Normalize(snapshotAt);
        var tenantId = await TenantOfAsync(connectorId, ct);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await LockSourceAsync(tenantId, connectorId, ct);
        if (await WatermarkAsync(connectorId, ct) != marker)
        {
            await tx.CommitAsync(ct);
            _log?.LogInformation(
                "Ausência do conector {ConnectorId} não publicada: uma fotografia mais recente da fonte já foi publicada.",
                connectorId);
            return new AbsenceOutcome(false, true, 0, new HashSet<Guid>(), 0);
        }

        await HitAsync(CheckpointAbsenceLocked, ct);

        // Com a marca da passada ainda sendo a da fonte, todo binding que não carrega ESTA marca foi observado só por
        // passadas anteriores (as mais novas teriam movido a marca) — é exatamente o ausente desta fotografia completa.
        var stale = _db.AssetSourceBindings
            .Where(b => b.ConnectorConfigId == connectorId && b.IsActive && b.LastObservedAt != marker);
        var assetIds = (await stale.Select(b => b.AssetId).Distinct().ToListAsync(ct)).ToHashSet();
        var deactivated = await stale.ExecuteUpdateAsync(s => s
            .SetProperty(b => b.IsActive, false)
            .SetProperty(b => b.ResolvedAt, marker), ct);
        var companionCount = companion is null ? 0 : await companion(ct);

        await tx.CommitAsync(ct);
        return new AbsenceOutcome(true, false, deactivated, assetIds, companionCount);
    }

    /// <summary>
    /// Executa <paramref name="work"/> (um lote de presença de fatos da fonte) numa transação, sob a trava da fonte,
    /// SOMENTE se a marca desta passada ainda for a da fonte. Devolve <c>false</c> — sem executar — quando uma
    /// fotografia mais recente já foi publicada: a passada atrasada para de publicar e não regride o que a mais
    /// nova afirmou. Lotes continuam lotes: a trava vale só pela duração de cada um.
    /// </summary>
    public async Task<bool> RunIfCurrentAsync(
        Guid connectorId, DateTimeOffset snapshotAt, Func<CancellationToken, Task> work, CancellationToken ct)
    {
        var marker = DeviceSnapshotMarker.Normalize(snapshotAt);
        var tenantId = await TenantOfAsync(connectorId, ct);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await LockSourceAsync(tenantId, connectorId, ct);
        if (await WatermarkAsync(connectorId, ct) != marker)
        {
            await tx.CommitAsync(ct);
            return false;
        }

        await work(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Recalcula o agregado dos ativos DESCOBERTOS por conector a partir dos bindings ativos de TODAS as fontes: o
    /// ativo fica ativo enquanto QUALQUER fonte o observar. Ativos manuais/CMDB não são tocados. Em lotes, cada um
    /// numa transação que trava as linhas dos ativos em ordem (<c>FOR NO KEY UPDATE</c>) ANTES de reler os
    /// bindings — dois recálculos do mesmo ativo (ex.: Defender e Intune) se serializam, e o último enxerga a última
    /// mudança confirmada. Devolve quantos ativos passaram de ativo para inativo.
    /// </summary>
    public async Task<int> RecomputeAssetsAsync(IEnumerable<Guid> assetIds, CancellationToken ct)
    {
        var deactivated = 0;
        foreach (var ids in OrderedForLocking(assetIds).Chunk(AssetBatchSize))
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var assets = await LockAssetsAsync(ids, ct);
            var idList = ids.ToList();
            var activeBindings = await _db.AssetSourceBindings
                .AsNoTracking()
                .Where(b => idList.Contains(b.AssetId) && b.IsActive)
                .Select(b => new { b.AssetId, b.SourceLastSeenAt, b.LastObservedAt })
                .ToListAsync(ct);
            await HitAsync(CheckpointRecomputeLocked, ct);
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
            await tx.CommitAsync(ct);
            _db.ChangeTracker.Clear();
        }
        return deactivated;
    }

    // ---- Passada ------------------------------------------------------------------------------------------------

    private async Task<ResolutionOutcome> ResolveAttemptAsync(
        Guid connectorId, string sourceLabel, string? ns,
        List<DeviceSourceObservation> observations, int receivedCount, int rejected,
        DateTimeOffset marker, CancellationToken ct)
    {
        var tenantId = await TenantOfAsync(connectorId, ct);
        var label = TrimTo(sourceLabel, MaxSourceLabelLength) ?? "Fonte integrada";

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Ordem fixa das travas: fonte → diretório. A da fonte serializa passadas do MESMO conector (presença,
        // ausência, marca); a do diretório serializa a decisão das chaves entre fontes (Defender × Intune).
        await LockSourceAsync(tenantId, connectorId, ct);
        if (ns is not null && _db.Database.IsNpgsql())
        {
            // Sem namespace não há chave forte a criar, e não há o que serializar entre fontes além do que o índice
            // natural do binding já garante. SQLite serializa escritores na própria conexão.
            await _db.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock({0})",
                new object[] { DeviceDirectoryLock.AdvisoryKey(tenantId, ns) }, ct);
        }

        // (0) PRECEDÊNCIA: uma fotografia mais recente desta fonte já foi publicada → esta passada chegou atrasada e
        // não escreve nada (nem presença, nem reativação, nem marca). Senão, esta marca passa a ser a da fonte.
        var watermark = await WatermarkAsync(connectorId, ct);
        if (watermark is { } published && marker < published)
        {
            await tx.CommitAsync(ct);
            _log?.LogInformation(
                "Resolução de dispositivos do conector {ConnectorId} superada por uma fotografia mais recente da fonte — nada publicado.",
                connectorId);
            return new ResolutionOutcome(
                new Dictionary<string, Guid>(), new HashSet<Guid>(),
                EmptyCounts(receivedCount, rejected) with { Superseded = true }, marker);
        }
        if (watermark != marker)
            await _db.Connectors
                .Where(c => c.Id == connectorId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.DeviceSnapshotWatermark, (DateTimeOffset?)marker), ct);

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
            // Identificadores contraditórios na MESMA coleta: nenhum deles vincula nem confirma vínculo.
            var contradictory = idObs.IsContradictory;
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
                ApplyObservation(binding, o, idObs, label, marker);
                if (contradictory) SetContradiction(binding, ns, idObs);
                else EvaluateExisting(binding, ns, key, evidenceState, keyByValue, keyByAsset, connectorId, label, marker, tally);
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
                    var asset = NewAsset(o, label, marker);
                    _db.Assets.Add(asset);
                    createdAssetIds.Add(asset.Id);
                    tally.AssetsCreated++;
                    assetId = asset.Id;
                    if (key is not null)
                    {
                        EstablishKey(ns!, key, assetId, connectorId, label, marker, keyByValue, keyByAsset);
                        tally.KeysEstablished++;
                        linked = true;
                    }
                }

                binding = new AssetSourceBinding
                {
                    AssetId = assetId,
                    ConnectorConfigId = connectorId,
                    ExternalId = o.ExternalId,
                    FirstObservedAt = marker,
                };
                ApplyObservation(binding, o, idObs, label, marker);
                if (linked) SetLinked(binding, ns!, key!, marker);
                else if (contradictory) SetContradiction(binding, ns, idObs);
                else SetEvidenceState(binding, ns, evidenceState);
                _db.AssetSourceBindings.Add(binding);
                bindingByExternalId[o.ExternalId] = binding;
                tally.BindingsCreated++;
            }

            if (contradictory) tally.ContradictoryIdentifiers++;
            tally.Count(binding.ResolutionState);
            assetByExternalId[o.ExternalId] = binding.AssetId;
            observedAssetIds.Add(binding.AssetId);

            var observedName = TrimTo(o.DisplayName, MaxNameLength);
            if (observedName is not null && !createdAssetIds.Contains(binding.AssetId))
                nameCandidates.TryAdd(binding.AssetId, observedName);
        }

        // (4) Nome PROVISÓRIO substituído pelo primeiro nome observado — e somente ele. Nome curado, manual ou
        // legado (NameOrigin.Unspecified) nunca é tocado; responsável, criticidade e relações também não. As linhas
        // são travadas na MESMA ordem do recálculo, para que as duas escritas no ativo nunca se bloqueiem em ciclo.
        foreach (var chunk in OrderedForLocking(nameCandidates.Keys).Chunk(LookupChunk))
        {
            var placeholders = (await LockAssetsAsync(chunk, ct))
                .Where(a => a.NameOrigin == AssetNameOrigin.Placeholder);
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
                "Resolução de dispositivos do conector {ConnectorId}: {Conflicts} conflito(s) preservado(s) para análise, {Contradictory} com identificadores contraditórios na mesma coleta (nenhum ativo escolhido arbitrariamente).",
                connectorId, tally.Conflicts, tally.ContradictoryIdentifiers);
        if (rejected > 0)
            _log?.LogWarning(
                "Resolução de dispositivos do conector {ConnectorId}: {Rejected} registro(s) com id na fonte fora do contrato recusado(s); nenhuma ausência será publicada nesta passada.",
                connectorId, rejected);
        _log?.LogInformation(
            "Resolução de dispositivos do conector {ConnectorId}: {Observed} observado(s), {Linked} vinculado(s) por identificador de diretório, {Created} ativo(s) criado(s), {Keys} chave(s) estabelecida(s), {NoId} sem identificador, {Invalid} com identificador inválido, {Unconfirmed} com diretório não confirmado.",
            connectorId, receivedCount, tally.Linked, tally.AssetsCreated, tally.KeysEstablished,
            tally.WithoutIdentifier, tally.InvalidIdentifier, tally.DirectoryUnconfirmed);

        return new ResolutionOutcome(
            assetByExternalId,
            observedAssetIds,
            new DeviceResolutionSyncResult(
                Observed: receivedCount,
                AssetsCreated: tally.AssetsCreated,
                BindingsCreated: tally.BindingsCreated,
                Linked: tally.Linked,
                KeysEstablished: tally.KeysEstablished,
                WithoutIdentifier: tally.WithoutIdentifier,
                InvalidIdentifier: tally.InvalidIdentifier,
                DirectoryUnconfirmed: tally.DirectoryUnconfirmed,
                Conflicts: tally.Conflicts,
                BindingsDeactivated: 0,
                DeactivationApplied: false,
                RejectedObservations: rejected,
                ContradictoryIdentifiers: tally.ContradictoryIdentifiers),
            marker);
    }

    /// <summary>"Estabelecido" = este binding já foi vinculado por um identificador e continua respondendo por ele,
    /// mesmo quando uma observação posterior o contradisse.</summary>
    private static bool IsEstablished(AssetSourceBinding b) =>
        b.DirectoryDeviceId is not null && b.DirectoryNamespace is not null
        && (b.ResolutionState == AssetBindingResolutionState.Linked
            || (b.ResolutionState == AssetBindingResolutionState.Conflict
                && b.ConflictKind is AssetBindingConflictKind.IdentifierChanged
                    or AssetBindingConflictKind.DirectoryChanged
                    or AssetBindingConflictKind.DirectoryAndIdentifierChanged
                    or AssetBindingConflictKind.ContradictoryObservation));

    /// <summary>
    /// Reavalia um binding EXISTENTE. Ele nunca muda de ativo; o que muda é o que se pode afirmar sobre o vínculo.
    /// </summary>
    private void EvaluateExisting(
        AssetSourceBinding b, string? ns, string? key, AssetBindingResolutionState evidenceState,
        Dictionary<string, AssetStrongIdentifier> keyByValue, Dictionary<Guid, AssetStrongIdentifier> keyByAsset,
        Guid connectorId, string label, DateTimeOffset now, Tally tally)
    {
        var established = IsEstablished(b);

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
            var directoryChanged = !string.Equals(b.DirectoryNamespace, ns, StringComparison.Ordinal);
            var identifierChanged = !string.Equals(b.DirectoryDeviceId, key, StringComparison.Ordinal);
            if (directoryChanged || identifierChanged)
            {
                // CONTRADIÇÃO: a fonte passou a informar outro dispositivo e/ou outro diretório para o mesmo registro.
                // O binding NÃO é movido; o vínculo estabelecido fica, e o PAR observado (diretório, identificador)
                // vira referência de análise, separado dele.
                var kind = directoryChanged && identifierChanged
                    ? AssetBindingConflictKind.DirectoryAndIdentifierChanged
                    : directoryChanged
                        ? AssetBindingConflictKind.DirectoryChanged
                        : AssetBindingConflictKind.IdentifierChanged;
                SetConflict(b, kind, ns, key,
                    keyByValue.TryGetValue(key, out var other) && other.AssetId != b.AssetId ? other.AssetId : null);
                return;
            }
            // Mesmo diretório e identificador de sempre: segue para confirmar a chave (e encerrar um conflito anterior).
        }

        if (keyByValue.TryGetValue(key, out var held))
        {
            if (held.AssetId == b.AssetId) SetLinked(b, ns!, key, now);
            else SetConflict(b, AssetBindingConflictKind.IdentifierHeldByOtherAsset, ns, key, held.AssetId);
            return;
        }

        if (keyByAsset.TryGetValue(b.AssetId, out var assetKey)
            && !string.Equals(assetKey.IdentifierValue, key, StringComparison.Ordinal))
        {
            SetConflict(b, AssetBindingConflictKind.AssetHeldByOtherIdentifier, ns, key, null);
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
        ClearConflict(b);
        b.LinkedAt ??= now;
    }

    private static void SetEvidenceState(AssetSourceBinding b, string? ns, AssetBindingResolutionState state)
    {
        b.ResolutionState = state;
        b.DirectoryNamespace = ns;
        ClearConflict(b);
    }

    /// <summary>
    /// Identificadores contraditórios na mesma coleta: conflito declarado, nenhum valor usado. Um vínculo
    /// estabelecido antes (diretório e identificador do binding) fica intacto; sem vínculo, o registro segue sem ele.
    /// </summary>
    private static void SetContradiction(AssetSourceBinding b, string? ns, DirectoryDeviceIdObservation idObs)
    {
        var values = idObs.ContradictoryValueList.Take(MaxConflictValues).ToList();
        SetConflict(b, AssetBindingConflictKind.ContradictoryObservation, ns, observedKey: null, otherAssetId: null,
            observedValues: values.Count == 0 ? null : string.Join(",", values));
    }

    /// <summary>
    /// Marca o conflito preservando o PAR observado (<paramref name="observedNamespace"/>,
    /// <paramref name="observedKey"/>) separado do vínculo estabelecido — que não é alterado.
    /// </summary>
    private static void SetConflict(
        AssetSourceBinding b, AssetBindingConflictKind kind, string? observedNamespace, string? observedKey,
        Guid? otherAssetId, string? observedValues = null)
    {
        b.ResolutionState = AssetBindingResolutionState.Conflict;
        b.ConflictKind = kind;
        b.ConflictDirectoryNamespace = observedNamespace;
        b.ConflictDirectoryDeviceId = observedKey;
        b.ConflictObservedDeviceIds = observedValues;
        b.ConflictAssetId = otherAssetId;
    }

    private static void ClearConflict(AssetSourceBinding b)
    {
        b.ConflictKind = AssetBindingConflictKind.None;
        b.ConflictDirectoryNamespace = null;
        b.ConflictDirectoryDeviceId = null;
        b.ConflictObservedDeviceIds = null;
        b.ConflictAssetId = null;
    }

    private static DeviceResolutionSyncResult EmptyCounts(int observed, int rejected) =>
        new(observed, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, RejectedObservations: rejected);

    // ---- Coordenação no banco -------------------------------------------------------------------------------------

    /// <summary>
    /// Tenant do conector, lido sob o query filter do contexto: um conector de outro tenant simplesmente não existe
    /// aqui (fail-closed), e o tenant das travas vem do registro, não de quem chama.
    /// </summary>
    private async Task<Guid> TenantOfAsync(Guid connectorId, CancellationToken ct)
    {
        if (_tenantByConnector.TryGetValue(connectorId, out var cached)) return cached;
        var tenantId = await _db.Connectors.AsNoTracking()
            .Where(c => c.Id == connectorId)
            .Select(c => (Guid?)c.TenantId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                "Conector da resolução de dispositivos não encontrado no tenant do contexto (fail-closed).");
        _tenantByConnector[connectorId] = tenantId;
        return tenantId;
    }

    private Task<DateTimeOffset?> WatermarkAsync(Guid connectorId, CancellationToken ct) =>
        _db.Connectors.AsNoTracking()
            .Where(c => c.Id == connectorId)
            .Select(c => c.DeviceSnapshotWatermark)
            .FirstAsync(ct);

    /// <summary>Trava consultiva de TRANSAÇÃO do ciclo de vida da fonte (PostgreSQL; SQLite serializa escritores).</summary>
    private async Task LockSourceAsync(Guid tenantId, Guid connectorId, CancellationToken ct)
    {
        if (!_db.Database.IsNpgsql()) return;
        await _db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0})",
            new object[] { DeviceSourceLock.AdvisoryKey(tenantId, connectorId) }, ct);
    }

    /// <summary>
    /// Ativos (rastreados) com as linhas travadas até o fim da transação, em ordem de <c>"Id"</c>. <c>FOR NO KEY
    /// UPDATE</c> não conflita com o <c>KEY SHARE</c> das FKs (inserir binding/exposição apontando para o ativo
    /// continua livre), mas serializa as escritas no próprio ativo. O query filter do tenant continua aplicado.
    /// </summary>
    private async Task<List<Asset>> LockAssetsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new List<Asset>();
        if (_db.Database.IsNpgsql())
            return await _db.Assets
                .FromSqlRaw("SELECT * FROM \"Assets\" WHERE \"Id\" = ANY({0}) ORDER BY \"Id\" FOR NO KEY UPDATE", ids.ToArray())
                .ToListAsync(ct);
        var list = ids.ToList();
        return await _db.Assets.Where(a => list.Contains(a.Id)).ToListAsync(ct);
    }

    /// <summary>
    /// Ordem de aquisição das travas de linha: a MESMA do <c>uuid</c> no PostgreSQL (bytes na ordem do texto
    /// canônico) — e não a de <see cref="Guid.CompareTo(Guid)"/> —, para que lotes de transações diferentes nunca
    /// adquiram as mesmas linhas em ordens cruzadas.
    /// </summary>
    private static List<Guid> OrderedForLocking(IEnumerable<Guid> ids) =>
        ids.Distinct().OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToList();

    private Task HitAsync(string checkpoint, CancellationToken ct) =>
        Checkpoint is null ? Task.CompletedTask : Checkpoint(checkpoint, ct);

    private sealed class Tally
    {
        public int AssetsCreated, BindingsCreated, KeysEstablished;
        public int Linked, WithoutIdentifier, InvalidIdentifier, DirectoryUnconfirmed, Conflicts, ContradictoryIdentifiers;

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
