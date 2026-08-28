using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Abstractions;

// ---- [AEGIS-AUD-020] Contrato genérico de ingestão de evidências (push SIEM/EDR) ----

/// <summary>Um lote de eventos normalizados recebido pelo endpoint genérico de ingestão.</summary>
public sealed record EvidenceBatch(string? SchemaVersion, IReadOnlyList<EvidenceEvent> Events);

/// <summary>
/// Um evento do lote, no vocabulário do EMISSOR — dado NÃO confiável. O cliente NÃO fornece TenantId,
/// subcategoria NIST, veredito, score, papel nem ConnectorConfigId: esses campos simplesmente não existem
/// aqui. O tenant e o mapping são resolvidos pelo servidor.
/// </summary>
public sealed record EvidenceEvent(
    string? EventId,
    string? SignalKey,
    string? EventType,
    string? Source,
    int? Severity,
    double? NumericValue,
    string? Unit,
    DateTimeOffset CollectedAt,
    string? RawPayloadJson);

/// <summary>
/// Um conector genérico de push JÁ AUTENTICADO pela sua própria chave de ingestão. Carrega o tenant
/// PROPRIETÁRIO (derivado do <see cref="ConnectorConfig"/>, nunca do chamador) — a única saída do boundary
/// cross-tenant. Toda persistência subsequente ocorre sob esse tenant, com query filter/stamping normais.
/// </summary>
public sealed record AuthenticatedConnector(Guid ConnectorId, Guid TenantId, ConnectorCapability Capability);

public enum PushOutcome
{
    /// <summary>Todos os eventos válidos e mapeados: persistidos (novos) ou já vistos (deduplicados).</summary>
    Accepted,

    /// <summary>Violação de contrato (schemaVersion ausente, lote vazio/grande demais, campo obrigatório ausente) → 400.</summary>
    ContractError,

    /// <summary>Ao menos um signalKey sem mapping conhecido → 422. Nada é persistido (all-or-nothing).</summary>
    Unmapped,
}

/// <summary>Desfecho do push: contagens e horário de recebimento; <see cref="Errors"/> descreve o 4xx.</summary>
public sealed record PushIngestionResult(
    PushOutcome Outcome,
    int Accepted,
    int Deduplicated,
    IReadOnlyList<string> Errors,
    DateTimeOffset ReceivedAt);

/// <summary>
/// Desfecho de uma coleta PULL executada pela autoridade única (mesmo executor/mapping do push).
/// <see cref="Vulnerabilities"/> e <see cref="Sentinel"/> são ADITIVOS (default null): preenchidos só por
/// conectores que implementam a capacidade correspondente, preservando os consumidores existentes.
/// </summary>
public sealed record PullIngestionResult(
    int Persisted, int Deduplicated, int Skipped, ConnectorStatus Status,
    VulnerabilitySyncResult? Vulnerabilities = null,
    SiemPostureSnapshot? Sentinel = null);

// ---- [AEGIS-MVP-MICROSOFT-SENTINEL] Postura operacional de SIEM (somente leitura) ----

/// <summary>
/// [AEGIS-MVP-MICROSOFT-SENTINEL] Fotografia NORMALIZADA e SEGURA da postura operacional de um SIEM (Microsoft
/// Sentinel via Log Analytics). SÓ agregados e instantes — NUNCA título de incidente, entidade, IP, host, usuário,
/// conteúdo de alerta, payload bruto, token ou segredo. É um FATO OPERACIONAL consultivo: NÃO vira EvidenceSignal,
/// NÃO alimenta o AEGIS Score e NÃO altera status/lifecycle de controle (a autoridade continua determinística).
///
/// Deliberadamente NÃO infere contenção, recuperação, comunicação, aderência a plano, sucesso de playbook, nem
/// tempo de reconhecimento/triagem (sem campo confiável), nem reabertura (arg_max entrega só o estado mais recente).
/// <see cref="IsComplete"/> é falso quando a fonte sinalizou resultado parcial/truncado — o chamador degrada a saúde.
/// </summary>
public sealed record SiemPostureSnapshot(
    int WindowDays,
    int IncidentsObserved,
    int OpenIncidents,
    int NewIncidents,
    int ClosedIncidents,
    int OpenHighSeverity,
    int OpenMediumSeverity,
    int OpenLowSeverity,
    int OpenInformationalSeverity,
    double? MeanTimeToCloseHours,
    int AlertsObserved,
    int AlertsHighSeverity,
    int AlertsMediumSeverity,
    DateTimeOffset? LastEvidenceAt,
    bool IsComplete);

/// <summary>
/// [AEGIS-MVP-MICROSOFT-SENTINEL] Capacidade COMPLEMENTAR a <see cref="IEvidenceConnector"/>: um conector de SIEM
/// que produz a POSTURA OPERACIONAL (somente leitura) via consultas fixas no servidor. O
/// <c>EvidenceIngestionExecutor</c> a detecta no MESMO fluxo pull e devolve a fotografia no resultado — SEM criar
/// EvidenceSignal, SEM mapear NIST e SEM tocar o score. Não quebra o contrato de <see cref="IEvidenceConnector"/>
/// (o conector pode não emitir sinais). O adaptador NUNCA escreve no banco — apenas devolve a fotografia normalizada.
/// </summary>
public interface ISiemPostureCollector
{
    ConnectorProvider Provider { get; }
    ConnectorCapability Capability { get; }

    /// <summary>Coleta a postura operacional do SIEM (só leitura). Falha da fonte é SANITIZADA e sobe.</summary>
    Task<SiemPostureSnapshot> CollectPostureAsync(ConnectorConfig config, CancellationToken ct);
}

/// <summary>
/// [AEGIS-AUD-020] Autentica o endpoint EXTERNO de ingestão pela CHAVE do conector (nunca por JWT). Faz o
/// ÚNICO lookup cross-tenant permitido: localiza o <see cref="ConnectorConfig"/> por id, valida a chave,
/// confirma que está habilitado e é um conector genérico de push (Generic/Siem ou Generic/Edr) e devolve o
/// tenant proprietário. Conector inexistente e chave inválida são INDISTINGUÍVEIS (mesmo <c>null</c>, tempo
/// ~constante) — não vaza existência de recurso alheio.
/// </summary>
public interface IConnectorIngestionAuthenticator
{
    Task<AuthenticatedConnector?> AuthenticateAsync(Guid connectorId, string presentedKey, CancellationToken ct);
}

/// <summary>
/// [AEGIS-AUD-020/041/043] Autoridade ÚNICA de execução/persistência de evidências — push e pull. Resolve o
/// mapping NIST (determinístico, via <see cref="INistSignalMapper"/>), protege o payload bruto, deduplica,
/// persiste a evidência e atualiza <c>LastSyncAt</c>/<c>LastStatus</c>. NUNCA pede ao LLM para decidir mapping
/// ou conformidade. O controller apenas valida o contrato/credencial e delega.
/// </summary>
public interface IEvidenceIngestionExecutor
{
    /// <summary>Persiste um lote de push sob o tenant do conector autenticado. All-or-nothing na validade.</summary>
    Task<PushIngestionResult> IngestPushAsync(AuthenticatedConnector connector, EvidenceBatch batch, CancellationToken ct);

    /// <summary>
    /// Executa a coleta PULL de um conector: resolve o adaptador, coleta, RE-MAPA via a autoridade central
    /// (ignorando os códigos trazidos pelo adaptador), persiste e carimba <c>LastSyncAt</c>/<c>LastStatus</c>.
    /// Devolve <c>null</c> quando não há adaptador para o par provider/capability (o controller responde 501).
    /// </summary>
    Task<PullIngestionResult?> CollectPullAsync(ConnectorConfig config, CancellationToken ct);
}

/// <summary>
/// [AEGIS-AUD-043] Autoridade determinística de mapeamento (Capability, SignalKey) → subcategorias NIST do
/// framework ATIVO. O cliente/adaptador não é autoridade; o LLM não escolhe nem inventa. Só devolve códigos
/// que EXISTEM no framework ativo; um sinal sem mapping fica AUSENTE do dicionário (o chamador rejeita/pula).
/// </summary>
public interface INistSignalMapper
{
    Task<IReadOnlyDictionary<string, SignalMappingResolution>> ResolveAsync(
        ConnectorCapability capability, IReadOnlyCollection<string> signalKeys, CancellationToken ct);
}

/// <summary>
/// [AEGIS-AUD-019] Resolução determinística de UM sinal: as subcategorias NIST mapeadas (autoridade =
/// <see cref="SignalMapping"/>) e o <see cref="SignalMapping.ScoringHint"/> — a regra determinística que
/// projeta a evidência no ledger. O hint pode ser <c>null</c> (mapping sem regra de scoring): a evidência
/// é persistida, mas NENHUM veredito é inventado (o controle segue NotEvaluated se não tiver outra avaliação).
/// </summary>
public sealed record SignalMappingResolution(IReadOnlyList<string> SubcategoryCodes, string? ScoringHint);

/// <summary>
/// [AEGIS-AUD-041] Proteção do payload BRUTO da evidência em repouso (Data Protection), com purpose PRÓPRIO
/// (<c>AegisScore.EvidenceSignal.RawPayload.v1</c>), distinto do purpose dos segredos de conector. O bruto
/// nunca é devolvido pela API/tela; o Unprotect existe para o motor de score (Entrega 3), não para saída.
/// </summary>
public interface IEvidenceRawPayloadProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}
