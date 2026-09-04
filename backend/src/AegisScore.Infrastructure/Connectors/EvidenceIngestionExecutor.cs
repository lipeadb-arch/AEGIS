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
using AegisScore.Application.Scoring;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Scoring;
using AppDetectionCoverage = AegisScore.Application.Abstractions.DetectionCoverageSnapshot;
using AppDevicePosture = AegisScore.Application.Abstractions.DevicePostureSnapshot;

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
    private readonly ILogger<ControlStateWriter> _writerLog;

    public EvidenceIngestionExecutor(
        DbContextOptions<AegisScoreDbContext> options,
        INistSignalMapper mapper,
        IEvidenceRawPayloadProtector payload,
        IConnectorRegistry registry,
        ILogger<EvidenceIngestionExecutor> log,
        ILogger<ControlStateWriter> writerLog)
    {
        _options = options;
        _mapper = mapper;
        _payload = payload;
        _registry = registry;
        _log = log;
        _writerLog = writerLog;
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
        var affectedCodes = new HashSet<string>(StringComparer.Ordinal);
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
                var ev = events[i];
                var resolution = mapped[ev.SignalKey!.Trim()];

                // Eventos DEDUPLICADOS também afetam seus controles: um retry da MESMA requisição (evidência
                // já no banco) pode estar reparando uma projeção que falhou antes. Registrar affectedCodes
                // AQUI — e não só no ramo aceito — é o que permite o recompute rodar de novo sem re-persistir.
                foreach (var code in resolution.SubcategoryCodes) affectedCodes.Add(code);

                var dedupKey = dedupKeys[i];
                if (existing.Contains(dedupKey) || !seen.Add(dedupKey))
                {
                    deduplicated++;   // reenvio (já no banco) ou duplicata INTRA-LOTE
                    continue;
                }

                var signal = BuildSignal(
                    connector.ConnectorId, ev, batch.SchemaVersion, resolution.SubcategoryCodes, dedupKey, receivedAt);

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

        // [AEGIS-AUD-019] Projeta a evidência no ledger DEPOIS de persistida. A evidência já está salva e
        // deduplicada; se a projeção falhar, NÃO a mascaramos como sucesso integral — carimbamos o conector
        // como Failed e PROPAGAMOS a falha operacional (500). O retry da MESMA requisição reencontra os
        // eventos já no banco (deduplicados, mas ainda contribuindo para affectedCodes) e REFAZ a projeção
        // sem duplicar EvidenceSignal; após a projeção bem-sucedida, o estado fica consistente.
        try
        {
            await ProjectScoreAsync(connector.TenantId, affectedCodes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex,
                "Projeção de score da ingestão do conector {ConnectorId} falhou; evidências persistidas, conector marcado como Failed (o retry deduplicado reprojeta).",
                connector.ConnectorId);
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

        // [AEGIS-MVP-POSTURE-02] UMA fotografia da fonte por sincronização. Quando o adaptador suporta coleta
        // COMBINADA (ICombinedEvidenceCollector), sinais E exposições vêm da MESMA aquisição — sem buscar a fonte
        // duas vezes e sem risco de sinais e findings virem de fotografias diferentes. Conectores que NÃO a
        // suportam seguem pelo contrato existente (CollectAsync + CollectFindingsAsync opcional). Uma única
        // try/catch: qualquer falha de coleta (sinais, exposições ou combinada) carimba Failed antes de persistir.
        List<EvidenceSignal> collected;
        PostureFindingCollection? findings = null;
        // [AEGIS-MVP-VULN-01] Coleta de vulnerabilidades associadas a ativos (máquinas × CVEs). Aditiva: um conector
        // pode implementar IEvidenceConnector sem emitir sinais e ainda produzir esta coleção (o Defender VM faz isso).
        VulnerabilityCollection? vulnerabilities = null;
        // [AEGIS-MVP-MICROSOFT-COVERAGE-01] Inventário/exposição de software — dimensão INDEPENDENTE, produzida SÓ
        // por conectores ICombinedVulnerabilityConnector (mesma aquisição/token/máquinas de Vulnerabilities). NUNCA
        // vira EvidenceSignal, NUNCA mapeia NIST e NUNCA toca o score — fato operacional/de exposição consultivo.
        SoftwareInventoryCollection? softwareInventory = null;
        // [AEGIS-MVP-SIEM] Postura operacional de SIEM PROVIDER-NEUTRAL (fato consultivo). Aditiva: o adaptador de
        // SIEM (Microsoft Sentinel, Google SecOps, …) não emite sinais de score e produz apenas esta fotografia.
        // Coletada na MESMA try/catch — NÃO vira EvidenceSignal, NÃO é reconciliada no banco e NÃO toca o AEGIS Score.
        SiemPostureSnapshot? siem = null;
        // [AEGIS-MVP-GOOGLE-SECOPS-02] Cobertura de detecção (regras × MITRE) — dimensão INDEPENDENTE de casos/alertas.
        // O coletor NÃO lança em falha da fonte (devolve estado classificado), então uma Rules API indisponível NÃO
        // derruba a sincronização de casos/alertas. Reconciliada no banco (agregado consultivo), NUNCA vira score.
        AppDetectionCoverage? detectionCoverage = null;
        // [AEGIS-MVP-MICROSOFT-COVERAGE-02] Postura de configuração/conformidade de dispositivos — DUAS dimensões
        // INDEPENDENTES (políticas configuradas e estado efetivo dos dispositivos). O coletor NÃO lança em falha da
        // fonte (devolve estados classificados por dimensão), então a ausência da permissão de dispositivos NÃO
        // derruba a sincronização. Reconciliada no banco (agregado consultivo), NUNCA vira EvidenceSignal/score.
        AppDevicePosture? devicePosture = null;
        try
        {
            if (adapter is ICombinedEvidenceCollector combined)
            {
                var all = await combined.CollectAllAsync(config, ct);
                collected = all.Signals.ToList();
                findings = all.Findings;
            }
            else
            {
                collected = new List<EvidenceSignal>();
                await foreach (var s in adapter.CollectAsync(config, ct))
                    collected.Add(s);

                // O adaptador NÃO escreve no banco — a reconciliação (upsert + resolução) ocorre adiante, no executor.
                if (adapter is IPostureFindingConnector findingConnector)
                    findings = await findingConnector.CollectFindingsAsync(config, ct);
            }

            // Vulnerabilidades (+ software, quando combinado): coletadas na MESMA try/catch — uma falha de
            // TRANSPORTE aqui carimba Failed ANTES de persistir. [AEGIS-MVP-MICROSOFT-COVERAGE-01] Quando o
            // adaptador suporta a capacidade COMBINADA, ela é PREFERIDA: token e /api/machines são adquiridos UMA
            // vez para as duas dimensões. O coletor combinado já isola falhas classificáveis de software (nunca
            // lança) e degrada — em vez de lançar — uma falha de transporte isolada na dimensão de vulnerabilidades,
            // então esta chamada só lança em falha de autenticação/máquinas (dependência dura de ambas as dimensões).
            if (adapter is ICombinedVulnerabilityConnector combinedVulnConnector)
            {
                var combinedVuln = await combinedVulnConnector.CollectVulnerabilitiesAndSoftwareAsync(config, ct);
                vulnerabilities = combinedVuln.Vulnerabilities;
                softwareInventory = combinedVuln.SoftwareInventory;
            }
            else if (adapter is IVulnerabilityFindingConnector vulnConnector)
            {
                vulnerabilities = await vulnConnector.CollectVulnerabilitiesAsync(config, ct);
            }

            // Postura operacional de SIEM (provider-neutral): também na MESMA try/catch. Uma falha da fonte carimba Failed.
            if (adapter is ISiemPostureCollector siemCollector)
                siem = await siemCollector.CollectPostureAsync(config, ct);

            // Cobertura de detecção (provider-neutral): dimensão INDEPENDENTE. O coletor devolve SEMPRE uma fotografia
            // com estado classificado (Available/Partial/Unavailable) — nunca lança por falha da fonte, então a Rules
            // API indisponível NÃO derruba casos/alertas nem carimba Failed. Só o cancelamento solicitado propaga.
            if (adapter is IDetectionCoverageCollector coverageCollector)
                detectionCoverage = await coverageCollector.CollectCoverageAsync(config, ct);

            // [AEGIS-MVP-MICROSOFT-COVERAGE-02] Postura de dispositivos (provider-neutral): dimensão INDEPENDENTE.
            // O coletor devolve SEMPRE uma fotografia com o estado de CADA dimensão classificado — nunca lança por
            // falha da fonte, então a falta de DeviceManagementManagedDevices.Read.All NÃO carimba Failed nem
            // invalida a leitura de políticas. Só o cancelamento solicitado propaga.
            if (adapter is IDevicePostureCollector devicePostureCollector)
                devicePosture = await devicePostureCollector.CollectDevicePostureAsync(config, ct);
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
        var affectedCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in collected)
        {
            if (!mapped.TryGetValue(s.SignalKey?.Trim() ?? "", out var resolution))
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
                MappedSubcategoryCodes = resolution.SubcategoryCodes.ToList(),
                CollectedAt = s.CollectedAt,
                ReceivedAt = now,
                ProtectedRawPayload = protectedRaw,
                JsonValue = null,   // nunca guarda bruto legível
                // DeduplicationKey NULL: snapshots pull são série temporal, não eventos idempotentes.
            });
            persisted++;
            foreach (var code in resolution.SubcategoryCodes) affectedCodes.Add(code);
        }

        // [AEGIS-MVP-VULN-01] Uma coleta de vulnerabilidades INCOMPLETA ou com registros inválidos degrada a saúde
        // do conector (Degraded) — honestidade operacional (a reconciliação, já ciente do IsComplete, não resolve
        // por omissão). Uma coleta COMPLETA sem achados segue Healthy. Falha real vira Failed no catch abaixo.
        var degradedByVuln = vulnerabilities is not null
            && (!vulnerabilities.IsComplete || vulnerabilities.InvalidMachines > 0
                || vulnerabilities.InvalidCves > 0 || vulnerabilities.InvalidRelations > 0);
        // [AEGIS-MVP-SIEM] Uma fotografia de SIEM PARCIAL/degradada (fonte sinalizou truncamento, ou uma dimensão
        // não comprovada) degrada a saúde — honestidade operacional. Uma fotografia completa (mesmo sem casos/alertas)
        // segue Healthy. A completude é derivada de AMBAS as dimensões (casos e alertas).
        var degradedBySiem = siem is not null && !siem.IsComplete;
        // [AEGIS-MVP-GOOGLE-SECOPS-02] Cobertura de detecção parcial/indisponível degrada a saúde — honestidade
        // operacional (ex.: sem `chronicle.rules.list`, a dimensão fica indisponível, mas casos/alertas seguem).
        var degradedByCoverage = detectionCoverage is not null && !detectionCoverage.IsComplete;
        // [AEGIS-MVP-MICROSOFT-COVERAGE-01] Software é dimensão ADICIONAL: sua ausência/degradação NUNCA vira
        // Failed (a autoridade continua sendo machines/vulnerabilities), mas também não deixa o conector
        // "operacional" silenciosamente quando só vulnerabilidades funcionou — rebaixa para Degraded, como as
        // demais dimensões aditivas (vulnerabilidades/SIEM/cobertura de detecção) já fazem.
        var degradedBySoftware = softwareInventory is not null && !softwareInventory.IsComplete;
        // [AEGIS-MVP-MICROSOFT-COVERAGE-02] O conector do Intune só é "plenamente operacional" com AS DUAS
        // dimensões completas. Sem DeviceManagementManagedDevices.Read.All ele fica Degraded — nunca Healthy com
        // uma dimensão bloqueada, e nunca Failed (a dimensão de políticas segue válida e utilizável).
        var degradedByDevicePosture = devicePosture is not null && !devicePosture.IsComplete;
        var status = (skipped > 0 || degradedByVuln || degradedBySiem || degradedByCoverage || degradedBySoftware
                || degradedByDevicePosture)
            ? ConnectorStatus.Degraded : ConnectorStatus.Healthy;
        // Um único SaveChanges: os sinais adicionados + o carimbo de sync são o MESMO fato.
        await StampConnectorAsync(db, config.Id, now, status, ct);

        // [AEGIS-MVP-POSTURE-02] Reconcilia as exposições coletadas (upsert idempotente + resolução em coleta
        // completa) sob o tenant proprietário. Falha NÃO é mascarada: carimba Failed e propaga (mesma semântica
        // da projeção). Um contexto NOVO isola a reconciliação do change tracker dos sinais já persistidos.
        if (findings is not null)
        {
            try
            {
                await ReconcilePostureFindingsAsync(config.TenantId, config.Id, findings, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex,
                    "Reconciliação de exposições do conector {ConnectorId} falhou; conector marcado como Failed.",
                    config.Id);
                await TryStampFailedAsync(config.Id, config.TenantId, ct);
                throw;
            }
        }

        // [AEGIS-MVP-VULN-01] Reconcilia as vulnerabilidades coletadas (upsert idempotente de Asset/Threat/exposição +
        // resolução/desativação SÓ em coleta completa) sob o tenant proprietário. Falha NÃO é mascarada: carimba
        // Failed e propaga (mesma semântica da projeção/posture). Contexto NOVO isola do change tracker anterior.
        // NUNCA cria EvidenceSignal nem toca o AEGIS Score — vulnerabilidade é fato operacional/de exposição.
        VulnerabilitySyncResult? vulnResult = null;
        if (vulnerabilities is not null)
        {
            try
            {
                vulnResult = await ReconcileVulnerabilitiesAsync(config.TenantId, config.Id, vulnerabilities, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex,
                    "Reconciliação de vulnerabilidades do conector {ConnectorId} falhou; conector marcado como Failed.",
                    config.Id);
                await TryStampFailedAsync(config.Id, config.TenantId, ct);
                throw;
            }
        }

        // [AEGIS-MVP-MICROSOFT-COVERAGE-01] Reconcilia o inventário de software SOB o tenant proprietário, DEPOIS
        // da reconciliação de vulnerabilidades acima: reusa os AssetSourceBindings que ela ACABOU de normalizar
        // para as mesmas máquinas (nunca cria Asset por conta própria). Falha NÃO é mascarada: carimba Failed e
        // propaga — mas uma coleta já classificada como falha (InsufficientPermission/Unsupported/Unavailable) NÃO
        // lança aqui (o reconciliador só REGISTRA a tentativa e preserva os dados anteriores). NUNCA cria
        // EvidenceSignal nem toca o AEGIS Score.
        SoftwareInventorySyncResult? softwareResult = null;
        if (softwareInventory is not null)
        {
            try
            {
                softwareResult = await ReconcileSoftwareInventoryAsync(config.TenantId, config.Id, softwareInventory, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex,
                    "Reconciliação de inventário de software do conector {ConnectorId} falhou; conector marcado como Failed.",
                    config.Id);
                await TryStampFailedAsync(config.Id, config.TenantId, ct);
                throw;
            }
        }

        // [AEGIS-MVP-GOOGLE-SECOPS-02] Reconcilia a cobertura de detecção (snapshot agregado por tenant+conector), sob
        // o tenant proprietário. Contexto NOVO isola do change tracker dos sinais. Falha NÃO é mascarada (carimba
        // Failed e propaga, como as demais reconciliações). O reconciliador NUNCA cria EvidenceSignal nem toca o
        // score — é agregado CONSULTIVO. Uma coleta Unavailable NÃO é erro: preserva o snapshot anterior e retorna.
        if (detectionCoverage is not null)
        {
            try
            {
                await ReconcileDetectionCoverageAsync(config.TenantId, config.Id, detectionCoverage, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex,
                    "Reconciliação da cobertura de detecção do conector {ConnectorId} falhou; conector marcado como Failed.",
                    config.Id);
                await TryStampFailedAsync(config.Id, config.TenantId, ct);
                throw;
            }
        }

        // [AEGIS-MVP-MICROSOFT-COVERAGE-02] Reconcilia a postura de dispositivos (snapshot por tenant+conector, com
        // políticas e grupos AGREGADOS), sob o tenant proprietário. Contexto NOVO isola do change tracker dos
        // sinais. Falha NÃO é mascarada (carimba Failed e propaga, como as demais reconciliações). O reconciliador
        // NUNCA cria EvidenceSignal nem toca o score — é agregado CONSULTIVO. Uma dimensão que falhou NÃO é erro:
        // preserva os dados anteriores daquela dimensão e apenas registra o desfecho da tentativa.
        DevicePostureSyncResult? devicePostureResult = null;
        if (devicePosture is not null)
        {
            try
            {
                devicePostureResult = await ReconcileDevicePostureAsync(config.TenantId, config.Id, devicePosture, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex,
                    "Reconciliação da postura de dispositivos do conector {ConnectorId} falhou; conector marcado como Failed.",
                    config.Id);
                await TryStampFailedAsync(config.Id, config.TenantId, ct);
                throw;
            }
        }

        // [AEGIS-AUD-019] Projeta a evidência coletada no ledger (recompute GLOBAL from-newest). Semântica
        // coerente com o push: falha na projeção NÃO é mascarada — carimba Failed e propaga como 500. Uma
        // nova coleta (mesmo sync) refaz o recompute sobre a evidência mais nova.
        try
        {
            await ProjectScoreAsync(config.TenantId, affectedCodes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex,
                "Projeção de score da coleta do conector {ConnectorId} falhou; evidências persistidas, conector marcado como Failed.",
                config.Id);
            await TryStampFailedAsync(config.Id, config.TenantId, ct);
            throw;
        }

        return new PullIngestionResult(
            persisted, 0, skipped, status, vulnResult, siem, detectionCoverage, softwareResult, devicePostureResult);
    }

    // ---- Reconciliação de inventário de software (AEGIS-MVP-MICROSOFT-COVERAGE-01) -----------------

    /// <summary>
    /// Reconcilia a coleta de inventário de software (produtos/bindings/instalações), sob o tenant proprietário
    /// (<see cref="SystemTenantContext"/>): contexto NOVO (isolado do change tracker dos sinais/vulnerabilidades) +
    /// query filter fail-closed + stamping. A lógica de upsert/colapso/resolução vive no
    /// <see cref="SoftwareInventoryReconciler"/>; o adaptador nunca escreve no banco. NÃO cria EvidenceSignal.
    /// </summary>
    private async Task<SoftwareInventorySyncResult> ReconcileSoftwareInventoryAsync(
        Guid tenantId, Guid connectorId, SoftwareInventoryCollection collection, CancellationToken ct)
    {
        await using var db = new AegisScoreDbContext(_options, new SystemTenantContext(tenantId));
        var reconciler = new SoftwareInventoryReconciler(db, _log);
        return await reconciler.ReconcileAsync(connectorId, collection, ct);
    }

    // ---- Reconciliação da postura de dispositivos (AEGIS-MVP-MICROSOFT-COVERAGE-02) ---------------

    /// <summary>
    /// Reconcilia a fotografia de postura de dispositivos no snapshot por (tenant, conector), sob o tenant
    /// proprietário (<see cref="SystemTenantContext"/>): contexto NOVO (isolado do change tracker dos sinais) +
    /// query filter fail-closed + stamping. A substituição por dimensão/preservação vive no
    /// <see cref="DevicePostureReconciler"/>; o adaptador nunca escreve no banco. NUNCA cria EvidenceSignal —
    /// postura de dispositivos é agregado CONSULTIVO.
    /// </summary>
    private async Task<DevicePostureSyncResult> ReconcileDevicePostureAsync(
        Guid tenantId, Guid connectorId, AppDevicePosture posture, CancellationToken ct)
    {
        await using var db = new AegisScoreDbContext(_options, new SystemTenantContext(tenantId));
        var reconciler = new DevicePostureReconciler(db, _log);
        return await reconciler.ReconcileAsync(connectorId, posture, ct);
    }

    // ---- Reconciliação da cobertura de detecção (AEGIS-MVP-GOOGLE-SECOPS-02) -----------------------

    /// <summary>
    /// Reconcilia a fotografia de cobertura de detecção no snapshot por (tenant, conector), sob o tenant proprietário
    /// (<see cref="SystemTenantContext"/>): contexto NOVO (isolado do change tracker dos sinais) + query filter
    /// fail-closed + stamping. A substituição atômica/preservação vive no <see cref="DetectionCoverageReconciler"/>;
    /// o adaptador nunca escreve no banco. NUNCA cria EvidenceSignal — cobertura é agregado CONSULTIVO.
    /// </summary>
    private async Task ReconcileDetectionCoverageAsync(
        Guid tenantId, Guid connectorId, AppDetectionCoverage coverage, CancellationToken ct)
    {
        await using var db = new AegisScoreDbContext(_options, new SystemTenantContext(tenantId));
        var reconciler = new DetectionCoverageReconciler(db, _log);
        await reconciler.ReconcileAsync(connectorId, coverage, ct);
    }

    // ---- Reconciliação de vulnerabilidades associadas a ativos (AEGIS-MVP-VULN-01) ----------------

    /// <summary>
    /// Reconcilia a coleta de vulnerabilidades no domínio existente (Asset/Threat/AssetThreatExposure), sob o tenant
    /// proprietário (<see cref="SystemTenantContext"/>): contexto NOVO (isolado do change tracker dos sinais) + query
    /// filter fail-closed + stamping do SaveChanges. A lógica de upsert/colapso/resolução vive no
    /// <see cref="VulnerabilityReconciler"/>; o adaptador nunca escreve no banco. NÃO cria EvidenceSignal.
    /// </summary>
    private async Task<VulnerabilitySyncResult> ReconcileVulnerabilitiesAsync(
        Guid tenantId, Guid connectorId, VulnerabilityCollection collection, CancellationToken ct)
    {
        await using var db = new AegisScoreDbContext(_options, new SystemTenantContext(tenantId));
        var reconciler = new VulnerabilityReconciler(db, _log);
        return await reconciler.ReconcileAsync(connectorId, collection, ct);
    }

    // ---- Reconciliação de exposições de postura (AEGIS-MVP-POSTURE-02) ----------------------------

    /// <summary>
    /// Reconcilia as exposições coletadas no ledger de postura, sob o tenant proprietário
    /// (<see cref="SystemTenantContext"/>): contexto NOVO (isolado do change tracker dos sinais) + query filter
    /// fail-closed + stamping do SaveChanges. A lógica de upsert/resolução/reabertura vive no
    /// <see cref="PostureExposureReconciler"/>; o adaptador nunca escreve no banco.
    /// </summary>
    private async Task ReconcilePostureFindingsAsync(
        Guid tenantId, Guid connectorId, PostureFindingCollection findings, CancellationToken ct)
    {
        await using var db = new AegisScoreDbContext(_options, new SystemTenantContext(tenantId));
        var reconciler = new PostureExposureReconciler(db, _log);
        await reconciler.ReconcileAsync(connectorId, findings, ct);
    }

    // ---- Projeção determinística no ledger (AEGIS-AUD-019) ----------------------------------------

    /// <summary>
    /// Projeta a evidência ingerida nos controles AFETADOS pela autoridade ÚNICA de recomputo
    /// (<see cref="EvidenceTelemetryRecompute"/>, compartilhada com o reparo de estados legados) e pelo escritor
    /// ÚNICO do ledger (<see cref="ControlStateWriter"/>). Recompute-from-newest GLOBAL: para cada controle,
    /// considera TODOS os <c>EvidenceSignals</c> do tenant que mapeiam para ele — de QUALQUER conector, não só o
    /// que disparou este lote —, escolhe a evidência mais autoritativa (desempate estável, independente da ordem do
    /// banco) e aplica o veredito. Sem hint conhecido, nenhum veredito é inventado (o controle segue NotEvaluated).
    ///
    /// Opera sob o tenant do conector (SystemTenantContext): o Global Query Filter (fail-closed) restringe sinais
    /// E conectores a esse tenant — SEM IgnoreQueryFilters. Uma falha do escritor sobe para o chamador (que carimba
    /// Failed e propaga); como o escritor é idempotente, o retry reprojeta sem efeito colateral. O LLM não participa.
    /// </summary>
    private async Task ProjectScoreAsync(
        Guid tenantId, IReadOnlyCollection<string> affectedCodes, CancellationToken ct)
    {
        if (affectedCodes.Count == 0) return;

        await using var db = new AegisScoreDbContext(_options, new SystemTenantContext(tenantId));

        var verdicts = await new EvidenceTelemetryRecompute(db, _mapper).ComputeAsync(affectedCodes, ct);
        if (verdicts.Count == 0) return;   // nenhum controle afetado tem evidência com hint conhecido

        var writer = new ControlStateWriter(db, new SystemTenantContext(tenantId), _writerLog);
        foreach (var (code, verdict) in verdicts)
        {
            // Falha do escritor NÃO é mascarada: sobe para o chamador, que carimba Failed e propaga. O escritor
            // é idempotente (upsert determinístico), então o retry reprojeta o mesmo veredito sem efeito colateral.
            await writer.ApplyVerdictAsync(
                tenantId, code, verdict.Status, verdict.Reason, VerdictSource.Telemetry, ct: ct);
        }
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
