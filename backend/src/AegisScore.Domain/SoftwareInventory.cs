using System;

namespace AegisScore.Domain;

// ============================================================================
//  [AEGIS-MVP-MICROSOFT-COVERAGE-01] Inventário de software e exposição por produto
// ============================================================================
// Modelo PROVIDER-NEUTRAL que diferencia quatro conceitos que a fonte (Microsoft Defender) mistura numa única
// leitura: (1) o ATIVO/dispositivo — já existe (Asset/AssetSourceBinding); (2) o PRODUTO de software (vendor+nome,
// SEM versão — é o grão de GET /api/Software); (3) a INSTALAÇÃO desse produto NUM ativo (produto+versão observados
// NUM dispositivo — é o grão de GET /api/machines/SoftwareInventoryByMachine); (4) a EXPOSIÇÃO/risco operacional
// associado ao produto (weaknesses/publicExploit/activeAlert/exposedMachines/impactScore — agregados que o Defender
// já calcula POR PRODUTO, nunca por instalação individual).
//
// Espelha EXATAMENTE o padrão já usado por AssetSourceBinding/AssetThreatObservation: uma entidade CONSOLIDADA
// (SoftwareProduct) e uma de VÍNCULO por FONTE (SoftwareProductSourceBinding), para que nenhuma fonte possa encerrar
// a observação de outra e para aceitar, no futuro, Google/AWS/Intune/CMDB sem redesenho. A correlação automática
// entre IDs de provedores distintos NÃO faz parte desta entrega — a estrutura apenas PERMITE múltiplos bindings.
//
// Identidade determinística: um PRODUTO é identificado por (TenantId, VendorKey, NameKey) normalizados (trim +
// invariant lower) — é a única chave comum aos DOIS endpoints do Defender (o endpoint por máquina NÃO devolve o
// "id" do endpoint agregado). O "id" do Defender (ex.: "microsoft-_-edge") vive em SoftwareProductSourceBinding
// como o identificador externo daquela fonte — nunca é usado para correlacionar entre fontes distintas.
//
// Persistência é LATEST SNAPSHOT (upsert idempotente), igual à Evidence Fabric e à cobertura de detecção — SEM
// histórico append-only. Nunca persiste o payload bruto do Defender, hostname, machineId (já vive no
// AssetSourceBinding existente) ou qualquer PII.

/// <summary>
/// Estado EXPLÍCITO da coleta/completude de uma fotografia de inventário de software. Distingue o que uma contagem
/// sozinha confundiria: uma coleta COMPLETA com zero produtos (<see cref="Available"/>) NÃO é "nunca coletado" nem
/// "indisponível". Só <see cref="Available"/> autoriza resolver/desativar por omissão. Espelha o vocabulário já
/// usado por <see cref="DetectionCoverageCollectionState"/>/SiemCollectionState, com as DUAS classificações extras
/// que a permissão dedicada exige: falta de <c>Software.Read.All</c> (<see cref="InsufficientPermission"/>) e
/// licença/capacidade incompatível do tenant (<see cref="Unsupported"/>) — nunca confundidas com "indisponível".
/// </summary>
public enum SoftwareInventoryCollectionState
{
    /// <summary>Nunca coletado — não há fotografia (linha placeholder ou ausência de snapshot).</summary>
    NeverCollected = 0,
    /// <summary>Coleta COMPLETA — os totais/produtos são a verdade (zero ou mais).</summary>
    Available = 1,
    /// <summary>Coletado, mas truncado (teto defensivo) ou com registros inválidos/órfãos: os agregados são um PISO.</summary>
    Partial = 2,
    /// <summary>403 — permissão de aplicativo <c>Software.Read.All</c> ausente/não consentida. Não comprovada.</summary>
    InsufficientPermission = 3,
    /// <summary>Licença/capacidade do tenant não cobre o inventário de software (ex.: 402). Não comprovada.</summary>
    Unsupported = 4,
    /// <summary>Indisponível/erro de transporte/resposta inválida/throttling persistente. Não comprovada.</summary>
    Unavailable = 5,
}

/// <summary>
/// PRODUTO de software CONSOLIDADO (vendor + nome, SEM versão), tenant-scoped. Identidade natural determinística
/// = (TenantId, VendorKey, NameKey) — <see cref="VendorKey"/>/<see cref="NameKey"/> são a forma normalizada
/// (trim + invariant lower) de <see cref="Vendor"/>/<see cref="Name"/>, único jeito de correlacionar o produto
/// entre os dois endpoints do Defender (o endpoint por máquina não repete o "id" do endpoint agregado). Os campos
/// de exposição são DENORMALIZADOS — recomputados a partir dos bindings de fonte ATIVOS (mesmo idioma do
/// Asset.IsActive/LastSeenAt recomputados a partir de AssetSourceBinding) para leitura O(1) na lista.
/// </summary>
public class SoftwareProduct : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public string Vendor { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Forma normalizada (trim + invariant lower) de <see cref="Vendor"/> — componente da chave natural.</summary>
    public string VendorKey { get; set; } = "";
    /// <summary>Forma normalizada (trim + invariant lower) de <see cref="Name"/> — componente da chave natural.</summary>
    public string NameKey { get; set; } = "";

    /// <summary>Produto ativo (algum binding de fonte ativo)? Desativado ≠ excluído — histórico preservado.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    // ---- Exposição/risco DENORMALIZADO — recomputado dos bindings de fonte ATIVOS (OR/MAX entre fontes) ----
    /// <summary>Maior contagem de fraquezas relatada por uma fonte ativa (fato da fonte, nunca inferido pela IA).</summary>
    public int WeaknessesCount { get; set; }
    /// <summary>Alguma fonte ativa relata exploit público conhecido para este produto.</summary>
    public bool HasPublicExploit { get; set; }
    /// <summary>Alguma fonte ativa relata alerta ativo associado a este produto.</summary>
    public bool HasActiveAlert { get; set; }
    /// <summary>Maior contagem de dispositivos expostos relatada por uma fonte ativa.</summary>
    public int ExposedMachinesCount { get; set; }
    /// <summary>Maior score de impacto (na escala da fonte) relatado por uma fonte ativa. Nulo = nenhuma fonte informou.</summary>
    public double? ImpactScore { get; set; }
}

/// <summary>
/// Observação de UMA fonte (<see cref="ConnectorConfig"/>) sobre o <see cref="SoftwareProduct"/> consolidado —
/// espelha <see cref="AssetSourceBinding"/>. Guarda o identificador externo do produto NAQUELA fonte (o "id" do
/// Defender, ex.: "microsoft-_-edge") e os fatos de exposição TAL COMO aquela fonte os relatou. Chave natural
/// <c>(TenantId, ConnectorConfigId, ExternalProductId)</c> — índice único que torna o upsert idempotente uma
/// invariante de banco. NUNCA persiste o payload bruto.
/// </summary>
public class SoftwareProductSourceBinding : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Produto CONSOLIDADO ao qual esta fonte se refere (um produto pode ter vários bindings).</summary>
    public Guid SoftwareProductId { get; set; }
    public SoftwareProduct? SoftwareProduct { get; set; }

    /// <summary>Conector (fonte) que produziu o binding — a autoridade do ciclo de vida DESTE binding.</summary>
    public Guid ConnectorConfigId { get; set; }
    public ConnectorConfig? ConnectorConfig { get; set; }

    /// <summary>Id do produto NA FONTE (ex.: "microsoft-_-edge" no Defender). Compõe a chave natural.</summary>
    public string ExternalProductId { get; set; } = "";

    /// <summary>Vendor/nome OBSERVADOS por esta fonte (semeiam o produto consolidado; não recorrigem um já curado).</summary>
    public string? VendorObserved { get; set; }
    public string? NameObserved { get; set; }

    // ---- Fatos de exposição TAL COMO relatados por esta fonte (nunca inferidos) ----
    public int Weaknesses { get; set; }
    public bool PublicExploit { get; set; }
    public bool ActiveAlert { get; set; }
    public int ExposedMachines { get; set; }
    public double? ImpactScore { get; set; }

    public DateTimeOffset FirstObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastObservedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>O binding está ativo? Uma fonte ausente numa coleta COMPLETA desativa SÓ o próprio binding.</summary>
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>
/// INSTALAÇÃO/observação de um <see cref="SoftwareProduct"/> (com versão) num <see cref="Asset"/> existente,
/// relatada por UMA fonte — o grão de <c>GET /api/machines/SoftwareInventoryByMachine</c>. Distinta da exposição
/// (que é por produto, não por instalação): esta linha só registra PRESENÇA/versão/ciclo de vida. Reusa
/// <see cref="ObservationLifecycle"/> (mesmo vocabulário Open/Resolved de AssetThreatObservation). Chave natural
/// <c>(TenantId, ConnectorConfigId, AssetId, SoftwareProductId, Version)</c> — <see cref="Version"/> NUNCA nulo:
/// versão ausente na fonte vira <c>""</c> (sentinela documentada), preservando o índice único determinístico mesmo
/// quando o Defender não informa a versão (PostgreSQL trata NULL como sempre-distinto num índice único, o que
/// permitiria duplicatas silenciosas se a versão ausente fosse NULL).
/// </summary>
public class SoftwareInstallation : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid SoftwareProductId { get; set; }
    public SoftwareProduct? SoftwareProduct { get; set; }

    public Guid AssetId { get; set; }
    public Asset? Asset { get; set; }

    /// <summary>Conector (fonte) que produziu a observação — a autoridade do ciclo de vida DESTA instalação.</summary>
    public Guid ConnectorConfigId { get; set; }
    public ConnectorConfig? ConnectorConfig { get; set; }

    /// <summary>Versão OBSERVADA. <c>""</c> = versão não informada pela fonte (sentinela — nunca NULL).</summary>
    public string Version { get; set; } = "";

    public ObservationLifecycle LifecycleState { get; set; } = ObservationLifecycle.Open;

    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>
/// Snapshot ATUAL de inventário de software de UM conector, isolado por tenant — espelha
/// <see cref="DetectionCoverageSnapshot"/>. Chave natural <c>(TenantId, ConnectorConfigId)</c> como índice único:
/// o upsert idempotente é invariante de banco. Guarda o ESTADO/última tentativa e um CACHE de KPIs agregados (evita
/// recalcular contagens em toda leitura da tela) — os totais só são recomputados quando a reconciliação de fato
/// altera produtos/instalações. NUNCA guarda nome de dispositivo, machineId ou payload bruto.
/// </summary>
public class SoftwareInventorySnapshot : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid ConnectorConfigId { get; set; }

    /// <summary>Rótulo estável da fonte (ex.: "Microsoft Defender Vulnerability Management").</summary>
    public string Source { get; set; } = "";

    /// <summary>Estado dos DADOS ARMAZENADOS (produtos/instalações): Available ou Partial. Nunca os estados de falha.</summary>
    public SoftwareInventoryCollectionState CollectionState { get; set; } = SoftwareInventoryCollectionState.NeverCollected;

    /// <summary>Desfecho da tentativa MAIS RECENTE — pode ser de falha mesmo com dados completos preservados.</summary>
    public SoftwareInventoryCollectionState LastAttemptState { get; set; } = SoftwareInventoryCollectionState.NeverCollected;

    /// <summary>Instante da última TENTATIVA de coleta (sucesso, parcial ou falha).</summary>
    public DateTimeOffset LastAttemptAt { get; set; }

    /// <summary>Instante da última coleta que PRODUZIU os dados armazenados (Available/Partial). Nulo enquanto nunca houve dados.</summary>
    public DateTimeOffset? LastCollectionAt { get; set; }

    /// <summary>Detalhe SANITIZADO da última tentativa (ex.: "Permissão insuficiente — Software.Read.All ausente").</summary>
    public string? LastAttemptDetail { get; set; }

    // ---- Cache de KPIs dos DADOS armazenados (produtos/instalações ATIVOS deste conector) ----
    public int TotalProducts { get; set; }
    public int ProductsWithWeaknesses { get; set; }
    public int ProductsWithPublicExploit { get; set; }
    public int ProductsWithActiveAlert { get; set; }
    public int ExposedInstallations { get; set; }
}
