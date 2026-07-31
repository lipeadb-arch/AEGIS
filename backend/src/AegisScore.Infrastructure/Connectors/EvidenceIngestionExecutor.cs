using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
/// [AEGIS-AUD-020/041/043] Autoridade ÚNICA de execução/persistência de evidências — push e pull. Concentra
/// a orquestração que antes vivia no <c>ConnectorsController</c>: validação do contrato, resolução do mapping
/// NIST (determinístico), proteção do payload bruto, deduplicação, persistência da evidência e atualização de
/// LastSyncAt/LastStatus. NUNCA delega o mapping ou a conformidade ao LLM.
///
/// Toda persistência ocorre num <see cref="AegisScoreDbContext"/> ligado ao tenant PROPRIETÁRIO
/// (<see cref="SystemTenantContext"/>), com query filter/stamping normais — o mesmo padrão dos workers e do
/// <c>AuthService</c>. O push chega sem tenant ambiente (endpoint anônimo); o tenant vem só do conector
/// autenticado.
/// </summary>
public sealed class EvidenceIngestionExecutor : IEvidenceIngestionExecutor
{
    /// <summary>Versão de contrato SUPORTADA (v1). Um lote com outra versão é recusado no contrato (400).</summary>
    public const string SupportedSchemaVersion = "1";

    /// <summary>Teto de eventos por lote — limita o custo e a superfície de um push abusivo.</summary>
    public const int MaxEventsPerBatch = 500;

    private const int MaxSignalKeyLength = 200;
    private const int MaxSourceLength = 200;
    private const int MaxEventTypeLength = 200;
    private const int MaxEventIdLength = 200;
    private const int MaxUnitLength = 50;
    /// <summary>Teto de payload por evento (chars). O tamanho do REQUEST inteiro é limitado no endpoint.</summary>
    private const int MaxPayloadChars = 64 * 1024;
    private const int MinSeverity = 0;
    private const int MaxSeverity = 4;

    /// <summary>Nome estável do índice idempotente — o único cuja violação vira sucesso deduplicado.</summary>
    private const string IdempotencyIndexName = "UX_EvidenceSignal_Idempotency";

    /// <summary>Separador de unidade (U+001F): não aparece em texto normal, evitando colisão por concatenação.</summary>
    private const string Sep = "\u001F";

    private readonly DbContextOptions<AegisScoreDbContext> _options;
    private readonly INistSignalMapper _mapper;
    private readonly IEvidenceRawPayloadProtector _payload;
    private readonly IConnectorRegistry _registry;
    private readonly ILogger<EvidenceIngestionExecutor> _log;

    public EvidenceIngestionExecutor(
        DbContextOptions<AegisScoreDbContext> options,
        INistSignalMapper mapper,
        IEvidenceRawPayloadProtector payload,
        IConnectorRegistry registry,
        ILogger<EvidenceIngestionExecutor> log)
    {
        _options = options;
        _mapper = mapper;
        _payload = payload;
        _registry = registry;
        _log = log;
    }

    public async Task<PushIngestionResult> IngestPushAsync(
        AuthenticatedConnector connector, EvidenceBatch batch, CancellationToken ct)
    {
        var receivedAt = DateTimeOffset.UtcNow;

        // 1) CONTRATO do lote (sem tocar o banco). Qualquer violação → 400, nada é persistido.
        var contractErrors = ValidateContract(batch);
        if (contractErrors.Count > 0)
            return new PushIngestionResult(PushOutcome.ContractError, 0, 0, contractErrors, receivedAt);

        var events = batch.Events;

        // 2) MAPPING determinístico (autoridade central). ALL-OR-NOTHING: um único signalKey sem mapping
        //    recusa o lote INTEIRO com 422 e NÃO persiste nada — nunca se pede ao LLM para resolver.
        var keys = events.Select(e => e.SignalKey!.Trim()).ToList();
        var mapped = await _mapper.ResolveAsync(connector.Capability, keys, ct);
        var unmapped = keys.Where(k => !mapped.ContainsKey(k)).Distinct().ToList();
        if (unmapped.Count > 0)
            return new PushIngestionResult(PushOutcome.Unmapped, 0, 0, unmapped, receivedAt);

        // Chaves idempotentes por evento (uma vez).
        var dedupKeys = events.Select(DedupKey).ToList();

        // 3) PERSISTÊNCIA sob o tenant PROPRIETÁRIO (query filter/stamping normais desse tenant), só depois de
        //    contrato e mapping completos terem passado.
        await using var db = new AegisScoreDbContext(_options, new SystemTenantContext(connector.TenantId));

        var accepted = 0;
        var deduplicated = 0;
        try
        {
            // Pré-checagem: chaves JÁ existentes no par (tenant, conector). Cobre o reenvio (caso comum) sem
            // depender de exceção — funciona igual em PostgreSQL e SQLite. A corrida concorrente que escapa
            // daqui é barrada pelo índice único (tratada abaixo, por evento).
            var existing = (await db.Signals
                    .Where(s => s.ConnectorConfigId == connector.ConnectorId
                        && s.DeduplicationKey != null && dedupKeys.Contains(s.DeduplicationKey!))
                    .Select(s => s.DeduplicationKey!)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < events.Count; i++)
            {
                var dedupKey = dedupKeys[i];
                if (existing.Contains(dedupKey) || !seen.Add(dedupKey))
                {
                    deduplicated++;   // reenvio (já no banco) ou duplicata INTRA-LOTE
                    continue;
                }

                var ev = events[i];
                var signal = BuildSignal(
                    connector.ConnectorId, ev, batch.SchemaVersion, mapped[ev.SignalKey!.Trim()], dedupKey, receivedAt);

                db.Signals.Add(signal);
                try
                {
                    // Implicit transaction por evento: uma falha rola SÓ este evento e o contexto segue
                    // utilizável — não há transação de lote para "envenenar". Mesmo idioma do dedupe de
                    // GovernanceDocument.Sha256.
                    await db.SaveChangesAsync(ct);
                    accepted++;
                }
                catch (DbUpdateException ex) when (IsIdempotencyViolation(ex))
                {
                    // Corrida concorrente: outra requisição inseriu a MESMA chave entre a pré-checagem e aqui.
                    // Sucesso IDEMPOTENTE, não erro — destaca a entidade e conta como deduplicado.
                    db.Entry(signal).State = EntityState.Detached;
                    deduplicated++;
                }
            }

            await StampConnectorAsync(db, connector.ConnectorId, receivedAt, ConnectorStatus.Healthy, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Falha OPERACIONAL após a autenticação: a saúde do conector conta a verdade (Failed), sem
            // mascarar o erro como sucesso. Qualquer DbUpdateException que NÃO seja a violação idempotente
            // cai aqui (é relançada dentro do loop) e é tratada como falha real.
            _log.LogError(ex, "Falha ao persistir lote de ingestão do conector {ConnectorId}.", connector.ConnectorId);
            await TryStampFailedAsync(connector.ConnectorId, connector.TenantId, ct);
            throw;
        }

        return new PushIngestionResult(
            PushOutcome.Accepted, accepted, deduplicated, Array.Empty<string>(), receivedAt);
    }

    public async Task<PullIngestionResult?> CollectPullAsync(ConnectorConfig config, CancellationToken ct)
    {
        var adapter = _registry.Resolve(config.Provider, config.Capability);
        if (adapter is null) return null;   // sem adaptador para o par provider/capability → 501 no controller

        var now = DateTimeOffset.UtcNow;
        await using var db = new AegisScoreDbContext(_options, new SystemTenantContext(config.TenantId));

        List<EvidenceSignal> collected;
        try
        {
            collected = new List<EvidenceSignal>();
            await foreach (var s in adapter.CollectAsync(config, ct))
                collected.Add(s);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Coleta do conector {ConnectorId} falhou.", config.Id);
            await TryStampFailedAsync(config.Id, config.TenantId, ct);
            throw;
        }

        // RE-MAPA pela MESMA autoridade central — IGNORA os MappedSubcategoryCodes trazidos pelo adaptador.
        var mapped = await _mapper.ResolveAsync(config.Capability, collected.Select(s => s.SignalKey).ToList(), ct);

        var persisted = 0;
        var skipped = 0;
        foreach (var s in collected)
        {
            if (!mapped.TryGetValue(s.SignalKey?.Trim() ?? "", out var codes))
            {
                skipped++;   // sinal sem mapping conhecido: NÃO persiste (a autoridade central manda)
                continue;
            }

            // [AEGIS-AUD-041] Se o adaptador trouxe um payload em JsonValue (bruto), ele NÃO pode ficar
            // legível: protege e move para ProtectedRawPayload, sem duplicar o conteúdo em claro.
            var protectedRaw = string.IsNullOrWhiteSpace(s.JsonValue) ? null : _payload.Protect(s.JsonValue);

            db.Signals.Add(new EvidenceSignal
            {
                ConnectorConfigId = config.Id,
                SignalKey = s.SignalKey!.Trim(),
                NumericValue = s.NumericValue,
                Unit = s.Unit,
                Severity = s.Severity,
                MappedSubcategoryCodes = codes.ToList(),
                CollectedAt = s.CollectedAt,
                ReceivedAt = now,
                ProtectedRawPayload = protectedRaw,
                JsonValue = null,   // nunca guarda bruto legível
                // DeduplicationKey NULL: snapshots pull são série temporal, não eventos idempotentes.
            });
            persisted++;
        }

        var status = skipped > 0 ? ConnectorStatus.Degraded : ConnectorStatus.Healthy;
        // Um único SaveChanges: os sinais adicionados + o carimbo de sync são o MESMO fato.
        await StampConnectorAsync(db, config.Id, now, status, ct);

        return new PullIngestionResult(persisted, 0, skipped, status);
    }

    // ---- Contrato ---------------------------------------------------------------------------------

    private static List<string> ValidateContract(EvidenceBatch batch)
    {
        var errors = new List<string>();

        if (!string.Equals(batch.SchemaVersion?.Trim(), SupportedSchemaVersion, StringComparison.Ordinal))
            errors.Add($"schemaVersion não suportado — esperado \"{SupportedSchemaVersion}\".");

        if (batch.Events is null || batch.Events.Count == 0)
        {
            errors.Add("O lote precisa conter ao menos um evento.");
            return errors;
        }
        if (batch.Events.Count > MaxEventsPerBatch)
            errors.Add($"O lote excede o máximo de {MaxEventsPerBatch} eventos.");

        for (var i = 0; i < batch.Events.Count; i++)
        {
            var ev = batch.Events[i];

            Require(errors, ev.SignalKey, i, "signalKey", MaxSignalKeyLength);
            Require(errors, ev.Source, i, "source", MaxSourceLength);
            Require(errors, ev.EventType, i, "eventType", MaxEventTypeLength);

            if (ev.CollectedAt == default)
                errors.Add($"evento[{i}].collectedAt é obrigatório.");

            if (ev.Severity is { } sev && (sev < MinSeverity || sev > MaxSeverity))
                errors.Add($"evento[{i}].severity fora da faixa admitida ({MinSeverity}..{MaxSeverity}).");

            if (!string.IsNullOrEmpty(ev.EventId) && ev.EventId.Length > MaxEventIdLength)
                errors.Add($"evento[{i}].eventId excede {MaxEventIdLength} caracteres.");
            if (!string.IsNullOrEmpty(ev.Unit) && ev.Unit.Length > MaxUnitLength)
                errors.Add($"evento[{i}].unit excede {MaxUnitLength} caracteres.");
            if (ev.RawPayloadJson is { Length: > MaxPayloadChars })
                errors.Add($"evento[{i}].data excede {MaxPayloadChars} caracteres.");
        }
        return errors;
    }

    private static void Require(List<string> errors, string? value, int i, string field, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"evento[{i}].{field} é obrigatório.");
        else if (value.Trim().Length > max)
            errors.Add($"evento[{i}].{field} excede {max} caracteres.");
    }

    // ---- Persistência / dedupe --------------------------------------------------------------------

    private EvidenceSignal BuildSignal(
        Guid connectorId, EvidenceEvent ev, string? schemaVersion,
        IReadOnlyList<string> codes, string dedupKey, DateTimeOffset receivedAt) => new()
    {
        ConnectorConfigId = connectorId,
        SignalKey = ev.SignalKey!.Trim(),
        Source = Clamp(ev.Source, MaxSourceLength),
        EventType = Clamp(ev.EventType, MaxEventTypeLength),
        ExternalEventId = Clamp(ev.EventId, MaxEventIdLength),
        DeduplicationKey = dedupKey,
        NumericValue = ev.NumericValue,
        Unit = Clamp(ev.Unit, MaxUnitLength),
        Severity = ev.Severity,
        MappedSubcategoryCodes = codes.ToList(),
        SchemaVersion = Clamp(schemaVersion, 32),
        CollectedAt = ev.CollectedAt,
        ReceivedAt = receivedAt,
        ProtectedRawPayload = string.IsNullOrWhiteSpace(ev.RawPayloadJson) ? null : _payload.Protect(ev.RawPayloadJson),
        // TenantId é carimbado pelo StampTenant (fail-closed) contra o SystemTenantContext do executor.
    };

    /// <summary>
    /// Reconhece SOMENTE a violação do índice idempotente esperado — qualquer outro erro é falha operacional
    /// e sobe. PostgreSQL: unique_violation (23505) no índice NOMEADO. SQLite (bateria relacional de testes):
    /// EvidenceSignal tem UM único índice único (o idempotente), então "UNIQUE constraint failed" só pode ser ele.
    /// </summary>
    private static bool IsIdempotencyViolation(DbUpdateException ex)
    {
        if (ex.InnerException is PostgresException pg)
            return pg.SqlState == PostgresErrorCodes.UniqueViolation
                && string.Equals(pg.ConstraintName, IdempotencyIndexName, StringComparison.Ordinal);

        var inner = ex.InnerException;
        return inner is not null
            && inner.GetType().Name == "SqliteException"
            && inner.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Chave idempotente: eventId quando há (idempotência explícita), senão hash do conteúdo normalizado.</summary>
    private static string DedupKey(EvidenceEvent e)
    {
        if (!string.IsNullOrWhiteSpace(e.EventId))
            return Sha256Hex("id" + Sep + e.EventId.Trim());

        var canonical = string.Join(Sep,
            (e.SignalKey ?? "").Trim(),
            (e.EventType ?? "").Trim(),
            (e.Source ?? "").Trim(),
            e.NumericValue?.ToString(CultureInfo.InvariantCulture) ?? "",
            (e.Unit ?? "").Trim(),
            e.Severity?.ToString(CultureInfo.InvariantCulture) ?? "",
            e.CollectedAt.ToUniversalTime().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            e.RawPayloadJson ?? "");
        return Sha256Hex("content" + Sep + canonical);
    }

    private static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string? Clamp(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        return t.Length <= max ? t : t[..max];
    }

    /// <summary>Carrega o conector sob o contexto do tenant e carimba LastSyncAt/LastStatus, num SaveChanges.</summary>
    private static async Task StampConnectorAsync(
        AegisScoreDbContext db, Guid connectorId, DateTimeOffset syncAt, ConnectorStatus status, CancellationToken ct)
    {
        var config = await db.Connectors.FirstOrDefaultAsync(c => c.Id == connectorId, ct);
        if (config is null) return;
        config.LastSyncAt = syncAt;
        config.LastStatus = status;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Best-effort: marca LastStatus=Failed num contexto NOVO (o anterior pode estar em estado ruim).</summary>
    private async Task TryStampFailedAsync(Guid connectorId, Guid tenantId, CancellationToken ct)
    {
        try
        {
            await using var db = new AegisScoreDbContext(_options, new SystemTenantContext(tenantId));
            var config = await db.Connectors.FirstOrDefaultAsync(c => c.Id == connectorId, ct);
            if (config is null) return;
            config.LastStatus = ConnectorStatus.Failed;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Não foi possível carimbar LastStatus=Failed no conector {ConnectorId}.", connectorId);
        }
    }
}
