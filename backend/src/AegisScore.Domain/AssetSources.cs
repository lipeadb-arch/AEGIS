using System;

namespace AegisScore.Domain;

// ============================================================================
//  [AEGIS-MVP-VULN-01] Fundação MULTICLOUD para inventário e exposição por fonte
// ============================================================================
// Duas entidades PROVIDER-NEUTRAL que desacoplam o ATIVO consolidado (Asset) e a EXPOSIÇÃO consolidada
// (AssetThreatExposure) das FONTES que os observam. Um Asset pode ter vários bindings (Defender, Google,
// AWS…); uma exposição ativo×CVE pode ter várias observações. O identificador do provedor (ex.: machineId
// do Defender) vive SOMENTE no binding — nunca em Asset.ExternalRef, que continua sendo o vínculo CMDB/legado.
// [AEGIS-ENTITY-RESOLUTION-01] A resolução entre fontes passou a existir, e SÓ por vínculo forte: o binding
// preserva o identificador de dispositivo no diretório informado pela fonte e o estado da resolução; a chave
// forte → ativo vive em AssetStrongIdentifier. Nome, hostname ou IP nunca unificam.

/// <summary>
/// Vínculo de um <see cref="Asset"/> a UMA fonte de descoberta (um <see cref="ConnectorConfig"/>): guarda o
/// identificador externo do dispositivo NAQUELA fonte (ex.: machineId do Defender, id do managedDevice do Intune)
/// e os metadados OBSERVADOS. A chave natural é <c>(TenantId, ConnectorConfigId, ExternalId)</c> — índice único
/// que torna o upsert idempotente uma invariante de banco.
///
/// [AEGIS-ENTITY-RESOLUTION-01] FINALIDADE DELIMITADA: persiste também o identificador de dispositivo no diretório
/// (aadDeviceId/azureADDeviceId) e o namespace do diretório de origem, EXCLUSIVAMENTE para resolver o mesmo
/// dispositivo entre fontes. Esses valores são dados internos de resolução — não entram em log, resposta de IA,
/// relatório ou listagem pública (só no diagnóstico restrito). Continua NUNCA persistindo IP, usuário, e-mail,
/// número de série ou payload bruto.
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

    // ---- [AEGIS-ENTITY-RESOLUTION-01] Resolução entre fontes por vínculo forte --------------------------------

    /// <summary>Rótulo legível da fonte que produziu o binding (ex.: "Microsoft Intune"). Nulo em bindings legados.</summary>
    public string? SourceLabel { get; set; }

    /// <summary>
    /// Namespace do diretório de origem CONFIRMADO pela integração (tenant do Entra da credencial efetiva, GUID
    /// normalizado). Nulo = não confirmado, ou binding legado. Nunca deduzido de hostname, e-mail ou do tenant AEGIS.
    /// </summary>
    public string? DirectoryNamespace { get; set; }

    /// <summary>
    /// Identificador de dispositivo no diretório ESTABELECIDO para este binding (GUID normalizado). Uma vez
    /// estabelecido, uma observação contraditória NÃO o substitui: ela vira conflito
    /// (<see cref="ConflictDirectoryDeviceId"/>) e o binding permanece no ativo original.
    /// </summary>
    public string? DirectoryDeviceId { get; set; }

    /// <summary>O que a última observação disse sobre o identificador de diretório (informado, ausente, inválido).</summary>
    public DirectoryIdentifierStatus DirectoryIdStatus { get; set; } = DirectoryIdentifierStatus.NotEvaluated;

    /// <summary>Estado da resolução entre fontes deste binding.</summary>
    public AssetBindingResolutionState ResolutionState { get; set; } = AssetBindingResolutionState.NotEvaluated;

    /// <summary>Natureza da contradição, quando <see cref="ResolutionState"/> é Conflict.</summary>
    public AssetBindingConflictKind ConflictKind { get; set; } = AssetBindingConflictKind.None;

    /// <summary>Identificador de diretório OBSERVADO que contradiz o vínculo (referência de análise; dado interno).</summary>
    public string? ConflictDirectoryDeviceId { get; set; }

    /// <summary>
    /// Diretório de origem da observação que causou o conflito — o par (<see cref="ConflictDirectoryNamespace"/>,
    /// <see cref="ConflictDirectoryDeviceId"/>) fica SEPARADO do vínculo estabelecido
    /// (<see cref="DirectoryNamespace"/>, <see cref="DirectoryDeviceId"/>). Nulo em conflitos gravados antes desta
    /// coluna: o diretório daquela observação não foi registrado e não é inventado. Dado interno (diagnóstico).
    /// </summary>
    public string? ConflictDirectoryNamespace { get; set; }

    /// <summary>
    /// Identificadores VÁLIDOS e distintos que a mesma coleta trouxe para este registro quando eles se contradisseram
    /// (<see cref="AssetBindingConflictKind.ContradictoryObservation"/>): ordenados e separados por vírgula, no
    /// máximo <c>5</c>. Vazio quando a contradição foi entre um valor e ausência/invalidez. Dado interno (diagnóstico).
    /// </summary>
    public string? ConflictObservedDeviceIds { get; set; }

    /// <summary>Outro ativo envolvido na contradição (ex.: o que já detém a chave), quando houver.</summary>
    public Guid? ConflictAssetId { get; set; }

    /// <summary>Instante em que o binding foi vinculado pela chave forte pela primeira vez.</summary>
    public DateTimeOffset? LinkedAt { get; set; }

    /// <summary>Instante da última avaliação de resolução deste binding.</summary>
    public DateTimeOffset? ResolutionEvaluatedAt { get; set; }

    /// <summary>
    /// Conformidade COMO INFORMADA pela fonte de gestão de dispositivos (ex.: Intune), na última observação.
    /// Informação da fonte — não é veredito de segurança do AEGIS. Nulo quando a fonte não a fornece.
    /// </summary>
    public DeviceComplianceBucket? SourceCompliance { get; set; }

    /// <summary>Criptografia COMO INFORMADA pela fonte, na última observação. Nulo quando a fonte não a fornece.</summary>
    public DeviceEncryptionBucket? SourceEncryption { get; set; }
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
