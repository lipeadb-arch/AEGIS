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
/// <see cref="Vulnerabilities"/>, <see cref="Siem"/> e <see cref="DetectionCoverage"/> são ADITIVOS (default null):
/// preenchidos só por conectores que implementam a capacidade correspondente, preservando os consumidores
/// existentes. Os resumos de SIEM são PROVIDER-NEUTRAL (<see cref="SiemPostureSnapshot"/> e
/// <see cref="DetectionCoverageSnapshot"/>) — o mesmo contrato serve Microsoft Sentinel, Google SecOps e futuros
/// SIEMs; o rótulo da fonte vive dentro do próprio resumo. Casos/alertas e cobertura de detecção são dimensões
/// INDEPENDENTES: a falha de uma NÃO apaga a outra.
/// </summary>
public sealed record PullIngestionResult(
    int Persisted, int Deduplicated, int Skipped, ConnectorStatus Status,
    VulnerabilitySyncResult? Vulnerabilities = null,
    SiemPostureSnapshot? Siem = null,
    DetectionCoverageSnapshot? DetectionCoverage = null);

// ---- [AEGIS-MVP-SIEM] Postura operacional de SIEM — PROVIDER-NEUTRAL (somente leitura) ----

/// <summary>
/// [AEGIS-MVP-SIEM] Estado EXPLÍCITO e PROVIDER-NEUTRAL da coleta de UMA dimensão da postura de SIEM (casos/incidentes
/// OU alertas). Distingue situações que uma contagem sozinha confundiria: uma consulta bem-sucedida com zero
/// resultados (<see cref="Available"/>) NÃO é o mesmo que a dimensão não existir na fonte, acesso negado, throttling,
/// timeout, resposta inválida ou resultado parcial/truncado. Só <see cref="Available"/> permite ler as contagens
/// como verdade completa; em qualquer outro estado a UI mostra indisponibilidade, NUNCA "0" — as contagens da
/// dimensão ficam ANULÁVEIS (nunca zero sintético). Vale para Microsoft Sentinel, Google SecOps e futuros SIEMs.
/// </summary>
public enum SiemCollectionState
{
    /// <summary>Coleta executada com sucesso e COMPLETA — as contagens são confiáveis (zero ou mais).</summary>
    Available = 0,
    /// <summary>Coletada, mas a fonte sinalizou truncamento OU um limite defensivo impediu a coleta integral —
    /// os agregados obtidos são um PISO, não o total. Degrada a saúde.</summary>
    Partial = 1,
    /// <summary>A fonte/instância não oferece esta dimensão (não comprovada — nunca "zero").</summary>
    Unsupported = 2,
    /// <summary>Acesso negado (403/permissão insuficiente) — não comprovada.</summary>
    PermissionDenied = 3,
    /// <summary>Throttling (429) — não comprovada.</summary>
    Throttled = 4,
    /// <summary>Tempo esgotado/cancelamento não-solicitado — não comprovada.</summary>
    Timeout = 5,
    /// <summary>Indisponível/erro de transporte/resposta inválida — não comprovada.</summary>
    Unavailable = 6,
}

/// <summary>
/// [AEGIS-MVP-SIEM] Semântica do PERÍODO de uma dimensão — deliberadamente EXPLÍCITA para nunca confundir um
/// inventário instantâneo com uma janela temporal.
/// </summary>
public enum SiemPeriodKind
{
    /// <summary>Janela deslizante de <c>WindowDays</c> dias (início inclusivo, fim exclusivo, calculada no servidor).</summary>
    RollingWindow = 0,
    /// <summary>Inventário no instante da coleta — NÃO finge representar uma janela temporal (ex.: listagem de casos
    /// sem garantia de filtro por criação/atualização).</summary>
    CurrentInventory = 1,
}

/// <summary>[AEGIS-MVP-SIEM] Uma contagem por prioridade declarada pela fonte (ex.: casos por prioridade no Google SecOps).</summary>
public sealed record SiemPriorityCount(string Priority, int Count);

/// <summary>
/// [AEGIS-MVP-SIEM] Dimensão de CASOS/INCIDENTES da postura de SIEM. Só AGREGADOS e INSTANTES — nunca título,
/// descrição, entidade, usuário, comentário ou payload bruto. Todas as contagens são ANULÁVEIS: <c>null</c> = não
/// coletada/não aplicável (NUNCA zero sintético). <see cref="OpenByPriority"/> (distribuição por prioridade, quando
/// a fonte a fornece) e o desmembramento por severidade coexistem porque provedores diferentes expõem eixos
/// diferentes — cada um preenche o que tem e deixa o resto nulo.
/// </summary>
public sealed record SiemCasePosture(
    SiemCollectionState State,
    SiemPeriodKind Period,
    int? WindowDays,
    bool IsComplete,
    int? Observed,
    int? Open,
    int? New,
    int? Closed,
    int? OpenHighSeverity,
    int? OpenMediumSeverity,
    int? OpenLowSeverity,
    int? OpenInformationalSeverity,
    IReadOnlyList<SiemPriorityCount>? OpenByPriority,
    double? MeanTimeToCloseHours,
    DateTimeOffset? LastEvidenceAt);

/// <summary>
/// [AEGIS-MVP-SIEM] Dimensão de ALERTAS da postura de SIEM. Só AGREGADOS e INSTANTES — nunca ativo, usuário, IP,
/// título ou payload. Contagens ANULÁVEIS (null = não coletada/não aplicável, nunca zero sintético).
/// </summary>
public sealed record SiemAlertPosture(
    SiemCollectionState State,
    SiemPeriodKind Period,
    int? WindowDays,
    bool IsComplete,
    int? Observed,
    int? HighSeverity,
    int? MediumSeverity,
    DateTimeOffset? LastEvidenceAt);

/// <summary>
/// [AEGIS-MVP-SIEM] Fotografia NORMALIZADA, SEGURA e PROVIDER-NEUTRAL da postura operacional de um SIEM, modelada em
/// DUAS DIMENSÕES INDEPENDENTES: <see cref="Cases"/> (casos/incidentes) e <see cref="Alerts"/> (alertas). SÓ agregados
/// e instantes — NUNCA título, entidade, IP, host, usuário, conteúdo de alerta, payload bruto, token ou segredo. É um
/// FATO OPERACIONAL consultivo: NÃO vira EvidenceSignal, NÃO alimenta o AEGIS Score e NÃO altera status/lifecycle de
/// controle (a autoridade continua determinística). <see cref="Source"/> é o rótulo estável da origem
/// ("Microsoft Sentinel", "Google SecOps"). A falha de UMA dimensão NUNCA é convertida em zero: a outra dimensão
/// preserva seus agregados e a completude é derivada de AMBAS.
/// </summary>
public sealed record SiemPostureSnapshot(
    string Source,
    SiemCasePosture Cases,
    SiemAlertPosture Alerts)
{
    /// <summary>Completude derivada: só é completa quando AMBAS as dimensões o são. Um <c>false</c> degrada a saúde.</summary>
    public bool IsComplete => Cases.IsComplete && Alerts.IsComplete;
}

/// <summary>
/// [AEGIS-MVP-SIEM] Capacidade COMPLEMENTAR a <see cref="IEvidenceConnector"/>: um conector de SIEM que produz a
/// POSTURA OPERACIONAL (somente leitura) via consultas fixas no servidor. O <c>EvidenceIngestionExecutor</c> a detecta
/// no MESMO fluxo pull e devolve a fotografia PROVIDER-NEUTRAL no resultado — SEM criar EvidenceSignal, SEM mapear
/// NIST e SEM tocar o score. Não quebra o contrato de <see cref="IEvidenceConnector"/> (o conector pode não emitir
/// sinais). O adaptador NUNCA escreve no banco — apenas devolve a fotografia normalizada.
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
