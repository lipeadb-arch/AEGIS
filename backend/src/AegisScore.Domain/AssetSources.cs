using System;

namespace AegisScore.Domain;

// ============================================================================
//  [AEGIS-MVP-VULN-01] Fundação MULTICLOUD para inventário e exposição por fonte
// ============================================================================
// Duas entidades PROVIDER-NEUTRAL que desacoplam o ATIVO consolidado (Asset) e a EXPOSIÇÃO consolidada
// (AssetThreatExposure) das FONTES que os observam. Um Asset pode ter vários bindings (Defender, Google,
// AWS…); uma exposição ativo×CVE pode ter várias observações. O identificador do provedor (ex.: machineId
// do Defender) vive SOMENTE no binding — nunca em Asset.ExternalRef, que continua sendo o vínculo CMDB/legado.
// A correlação automática entre IDs de provedores distintos NÃO faz parte desta entrega: a estrutura apenas
// PERMITE que múltiplos bindings/observações apontem, no futuro, para o mesmo Asset/exposição.

/// <summary>
/// Vínculo de um <see cref="Asset"/> a UMA fonte de descoberta (um <see cref="ConnectorConfig"/>): guarda o
/// identificador externo do dispositivo NAQUELA fonte (ex.: machineId do Defender) e os metadados OBSERVADOS.
/// A chave natural é <c>(TenantId, ConnectorConfigId, ExternalId)</c> — índice único que torna o upsert idempotente
/// uma invariante de banco. NUNCA persiste IP, aadDeviceId, usuário ou payload bruto.
/// </summary>
public class AssetSourceBinding : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Ativo CONSOLIDADO ao qual esta fonte se refere (um Asset pode ter vários bindings).</summary>
    public Guid AssetId { get; set; }
    public Asset? Asset { get; set; }

    /// <summary>Conector (fonte) que produziu o binding — a autoridade do ciclo de vida DESTE binding.</summary>
    public Guid ConnectorConfigId { get; set; }
    public ConnectorConfig? ConnectorConfig { get; set; }

    /// <summary>Id do dispositivo NA FONTE (ex.: machineId do Defender). Compõe a chave natural com tenant+conector.</summary>
    public string ExternalId { get; set; } = "";

    /// <summary>Nome OBSERVADO pela fonte (ex.: computerDnsName). Semeia o nome de um Asset NOVO; não sobrescreve nome curado.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Plataforma/subtipo OBSERVADO (ex.: "Windows11"). Semeia o SubType de um Asset novo; não sobrescreve o curado.</summary>
    public string? SubType { get; set; }

    /// <summary>Instante da PRIMEIRA observação deste binding pelo AEGIS (preservado).</summary>
    public DateTimeOffset FirstObservedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Instante da coleta mais recente que OBSERVOU este binding.</summary>
    public DateTimeOffset LastObservedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Último "visto por último" informado PELA FONTE (ex.: lastSeen do Defender). Nulo quando ausente.</summary>
    public DateTimeOffset? SourceLastSeenAt { get; set; }

    /// <summary>O binding está ativo? Uma fonte ausente numa coleta COMPLETA desativa SÓ o próprio binding.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Instante em que o binding foi desativado (sumiu de uma coleta completa da fonte); null enquanto ativo.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>
/// Observação de UMA fonte (<see cref="ConnectorConfig"/>) sobre uma exposição CONSOLIDADA ativo×CVE
/// (<see cref="AssetThreatExposure"/>). Cada conector controla o ciclo de vida das SUAS observações; a exposição
/// consolidada fica efetivamente ABERTA enquanto QUALQUER observação estiver <see cref="ObservationLifecycle.Open"/>.
/// Chave natural <c>(TenantId, ConnectorConfigId, AssetThreatExposureId)</c> — índice único (invariante de banco).
/// O detalhe de produto/versão daquela fonte vive no <see cref="EvidenceJson"/> normalizado (nunca a resposta bruta).
/// </summary>
public class AssetThreatObservation : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Exposição CONSOLIDADA (ativo×CVE) observada por esta fonte.</summary>
    public Guid AssetThreatExposureId { get; set; }
    public AssetThreatExposure? AssetThreatExposure { get; set; }

    /// <summary>Conector (fonte) que produziu a observação — a autoridade do ciclo de vida DESTA observação.</summary>
    public Guid ConnectorConfigId { get; set; }
    public ConnectorConfig? ConnectorConfig { get; set; }

    /// <summary>Ciclo de vida DESTA observação (Open/Resolved) — controlado só por esta fonte.</summary>
    public ObservationLifecycle LifecycleState { get; set; } = ObservationLifecycle.Open;

    /// <summary>Instante da PRIMEIRA vez que esta fonte observou a exposição (preservado).</summary>
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Instante da coleta mais recente desta fonte que observou a exposição.</summary>
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Instante em que ESTA fonte resolveu a exposição (sumiu de sua coleta completa); null enquanto Open.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>
    /// Detalhe NORMALIZADO e permitido desta fonte (produtos/versões/fixingKb colapsados por machine×CVE), com
    /// <c>TotalProducts</c>/<c>ProductsTruncated</c> quando truncado — nunca truncamento silencioso, nunca resposta bruta.
    /// </summary>
    public string? EvidenceJson { get; set; }
}
