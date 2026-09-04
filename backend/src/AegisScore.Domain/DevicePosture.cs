using System;
using System.Collections.Generic;

namespace AegisScore.Domain;

// ============================================================================
//  [AEGIS-MVP-MICROSOFT-COVERAGE-02] Postura de configuração e conformidade de dispositivos
// ============================================================================
// Fotografia CONSULTIVA e PROVIDER-NEUTRAL de DUAS dimensões INDEPENDENTES de uma
// plataforma de gestão de dispositivos (a primeira fonte concreta é o Microsoft Intune):
//
//   (1) POSTURA CONFIGURADA — políticas de conformidade e configurações de dispositivo
//       que existem no tenant, com o estado de ATRIBUIÇÃO quando ele for objetivamente
//       comprovável pela API oficial;
//   (2) ESTADO EFETIVO DOS DISPOSITIVOS — como os dispositivos gerenciados de fato estão
//       (conformes, não conformes, em carência, desconhecidos, sincronizados ou obsoletos).
//
// As duas dimensões degradam SEPARADAMENTE: a ausência da permissão de dispositivos NÃO
// invalida a leitura de políticas, e vice-versa. Uma dimensão bloqueada/indisponível NUNCA
// vira zero — ela carrega um estado EXPLÍCITO.
//
// É um FATO OPERACIONAL: NÃO vira EvidenceSignal, NÃO alimenta o AEGIS Score, NÃO altera
// conformidade NIST nem os estados determinísticos dos controles. A existência de uma
// política não comprova controle implementado; um dispositivo "compliant" segundo a fonte
// não é prova suficiente de um controle NIST — por isso quantidade JAMAIS gera pontos.
//
// PRIVACIDADE: nada de userPrincipalName, nome/e-mail do usuário primário, número de série,
// IMEI/MEID, telefone, MAC (Wi-Fi/Ethernet), chave de recuperação, payload de política ou
// valor de configuração. Os dispositivos são persistidos SOMENTE como grupos AGREGADOS
// (sistema operacional × conformidade × criptografia × atividade) — nenhuma linha por
// dispositivo, nenhum identificador de dispositivo, nenhuma PII.

/// <summary>
/// Estado EXPLÍCITO de UMA dimensão de coleta da postura de dispositivos. Distingue o que uma contagem
/// sozinha confundiria: uma coleta COMPLETA com zero dispositivos (<see cref="Available"/>) NÃO é o mesmo
/// que uma dimensão bloqueada por permissão, sem licença, truncada ou nunca coletada.
///
/// Espelha DELIBERADAMENTE o vocabulário já consolidado em <see cref="SoftwareInventoryCollectionState"/> e
/// <see cref="DetectionCoverageCollectionState"/>, mas é um enum PRÓPRIO: cada um desses é persistido como
/// <c>int</c> por migrations já mergeadas e está semanticamente ligado à sua própria dimensão. Unificá-los
/// exigiria reescrever três pacotes já entregues — risco que esta entrega não introduz. Os nomes aqui são os
/// do vocabulário desta dimensão (<see cref="NotAuthorized"/>/<see cref="NotLicensed"/> em vez de
/// <c>InsufficientPermission</c>/<c>Unsupported</c>), porque é assim que a interface fala com o operador.
/// </summary>
public enum DevicePostureDimensionState
{
    /// <summary>Nunca coletada — não há fotografia desta dimensão (linha placeholder ou ausência de snapshot).</summary>
    NeverCollected = 0,

    /// <summary>Coleta COMPLETA — os totais desta dimensão são a verdade (zero ou mais).</summary>
    Available = 1,

    /// <summary>Coletada, mas truncada (teto defensivo) ou com registros inválidos: os agregados são um PISO.</summary>
    Partial = 2,

    /// <summary>403 — a permissão de aplicativo desta dimensão não foi concedida/consentida. NÃO comprovada, nunca "zero".</summary>
    NotAuthorized = 3,

    /// <summary>A licença/capacidade do tenant não cobre esta dimensão (ex.: sem licença Intune ativa). NÃO comprovada.</summary>
    NotLicensed = 4,

    /// <summary>Indisponível: transporte, throttling persistente, resposta fora do contrato, 401 ou 5xx. NÃO comprovada.</summary>
    Unavailable = 5,
}

/// <summary>Natureza de uma política de dispositivo persistida — as duas famílias que a fonte expõe separadamente.</summary>
public enum DevicePolicyKind
{
    /// <summary>Política de CONFORMIDADE (define o que torna um dispositivo conforme).</summary>
    CompliancePolicy = 0,

    /// <summary>Perfil de CONFIGURAÇÃO de dispositivo (define ajustes aplicados ao dispositivo).</summary>
    DeviceConfiguration = 1,
}

/// <summary>
/// Estado de ATRIBUIÇÃO de UMA política. <see cref="Unknown"/> é o valor HONESTO quando a fonte não permitiu
/// afirmar objetivamente a atribuição (o contrato oficial não devolveu a coleção) — jamais se assume
/// "não atribuída" por ausência de dado.
/// </summary>
public enum DevicePolicyAssignmentState
{
    /// <summary>Não foi possível afirmar objetivamente — a fonte não devolveu a coleção de atribuições.</summary>
    Unknown = 0,

    /// <summary>A fonte devolveu a coleção de atribuições e ela tem ao menos um alvo.</summary>
    Assigned = 1,

    /// <summary>A fonte devolveu a coleção de atribuições e ela está VAZIA — a política não alcança ninguém.</summary>
    Unassigned = 2,
}

/// <summary>Conformidade AGREGADA de um grupo de dispositivos, no vocabulário provider-neutral do AEGIS.</summary>
public enum DeviceComplianceBucket
{
    /// <summary>A fonte não avaliou/não sabe (inclui estados não reconhecidos). NUNCA contado como conforme.</summary>
    Unknown = 0,
    Compliant = 1,
    Noncompliant = 2,
    /// <summary>Não conforme, mas dentro do período de carência concedido pela política.</summary>
    InGracePeriod = 3,
    /// <summary>Políticas conflitantes impedem um veredito — não é conformidade.</summary>
    Conflict = 4,
    /// <summary>A avaliação falhou na fonte — não é conformidade.</summary>
    Error = 5,
    /// <summary>Avaliado por um gerenciador externo (co-gestão) — a fonte não afirma conformidade própria.</summary>
    ManagedExternally = 6,
}

/// <summary>Cobertura de criptografia AGREGADA. <see cref="Unknown"/> quando a fonte não informou o campo.</summary>
public enum DeviceEncryptionBucket { Unknown = 0, Encrypted = 1, NotEncrypted = 2 }

/// <summary>
/// Atividade AGREGADA por última sincronização com a plataforma. <see cref="Unknown"/> quando a fonte não
/// informou o instante — nunca se presume "obsoleto" nem "ativo" por ausência de dado.
/// </summary>
public enum DeviceActivityBucket { Unknown = 0, Active = 1, Stale = 2 }

/// <summary>
/// Snapshot ATUAL da postura de dispositivos de UM conector, isolado por tenant. Chave natural
/// (TenantId, ConnectorConfigId) — índice único que torna o upsert idempotente uma invariante de banco.
///
/// As duas dimensões têm colunas de estado/recência INDEPENDENTES: uma sincronização em que só a dimensão de
/// políticas funcionou preserva integralmente os dados de dispositivos coletados antes (e registra honestamente
/// que a tentativa de dispositivos falhou), e vice-versa.
/// </summary>
public class DevicePostureSnapshot : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Conector que produziu a fotografia (a fonte concreta desta postura).</summary>
    public Guid ConnectorConfigId { get; set; }

    /// <summary>Rótulo estável da fonte (ex.: "Microsoft Intune"). Nunca endpoint, credencial ou permissão.</summary>
    public string Source { get; set; } = "";

    // ---- Dimensão 1: POSTURA CONFIGURADA (políticas) --------------------------------------------

    /// <summary>Estado dos DADOS ARMAZENADOS de políticas: Available/Partial (ou NeverCollected). Nunca uma falha.</summary>
    public DevicePostureDimensionState ConfigurationState { get; set; } = DevicePostureDimensionState.NeverCollected;

    /// <summary>Desfecho da tentativa MAIS RECENTE desta dimensão (pode ser falha com dados anteriores preservados).</summary>
    public DevicePostureDimensionState ConfigurationAttemptState { get; set; } = DevicePostureDimensionState.NeverCollected;

    /// <summary>Instante da última TENTATIVA da dimensão de políticas (sucesso, parcial ou falha).</summary>
    public DateTimeOffset? ConfigurationAttemptAt { get; set; }

    /// <summary>Instante da última coleta que PRODUZIU os dados de políticas armazenados.</summary>
    public DateTimeOffset? ConfigurationCollectedAt { get; set; }

    public int CompliancePolicyCount { get; set; }
    public int DeviceConfigurationCount { get; set; }

    /// <summary>
    /// Estado da SUB-DIMENSÃO de atribuição. Independente de <see cref="ConfigurationState"/>: as políticas podem
    /// estar disponíveis enquanto a atribuição permanece não comprovável pelo contrato da fonte.
    /// </summary>
    public DevicePostureDimensionState AssignmentState { get; set; } = DevicePostureDimensionState.NeverCollected;

    public int PoliciesAssigned { get; set; }
    public int PoliciesUnassigned { get; set; }
    /// <summary>Políticas cuja atribuição a fonte não permitiu afirmar. Nunca somadas a "não atribuídas".</summary>
    public int PoliciesAssignmentUnknown { get; set; }

    /// <summary>Fingerprint determinístico dos DADOS de políticas — impede reescrever os filhos sem mudança.</summary>
    public string ConfigurationFingerprint { get; set; } = "";

    // ---- Dimensão 2: ESTADO EFETIVO DOS DISPOSITIVOS --------------------------------------------

    /// <summary>Estado dos DADOS ARMAZENADOS de dispositivos: Available/Partial (ou NeverCollected). Nunca uma falha.</summary>
    public DevicePostureDimensionState DeviceState { get; set; } = DevicePostureDimensionState.NeverCollected;

    /// <summary>Desfecho da tentativa MAIS RECENTE desta dimensão (ex.: NotAuthorized sem a permissão de dispositivos).</summary>
    public DevicePostureDimensionState DeviceAttemptState { get; set; } = DevicePostureDimensionState.NeverCollected;

    public DateTimeOffset? DeviceAttemptAt { get; set; }
    public DateTimeOffset? DeviceCollectedAt { get; set; }

    public int TotalDevices { get; set; }
    public int CompliantDevices { get; set; }
    public int NoncompliantDevices { get; set; }
    public int InGracePeriodDevices { get; set; }
    public int ConflictDevices { get; set; }
    public int ErrorDevices { get; set; }
    public int ManagedExternallyDevices { get; set; }
    public int UnknownComplianceDevices { get; set; }

    public int EncryptedDevices { get; set; }
    public int NotEncryptedDevices { get; set; }
    public int UnknownEncryptionDevices { get; set; }

    public int ActiveDevices { get; set; }
    public int StaleDevices { get; set; }
    public int UnknownActivityDevices { get; set; }

    /// <summary>Janela (em dias) a partir da qual um dispositivo é considerado obsoleto por falta de sincronização.</summary>
    public int StaleThresholdDays { get; set; }

    /// <summary>
    /// Quantos dispositivos trazem um identificador de dispositivo de diretório (Entra) na fonte. É um FATO de
    /// CORRELAÇÃO, não um identificador: o valor em si NÃO é persistido (o AEGIS não o preserva do lado do
    /// Defender, então a correlação determinística ainda não é possível — a lacuna é registrada, não estimada).
    /// </summary>
    public int DevicesWithDirectoryId { get; set; }

    /// <summary>Fingerprint determinístico dos DADOS de dispositivos — impede reescrever os grupos sem mudança.</summary>
    public string DeviceFingerprint { get; set; } = "";

    public List<DevicePosturePolicy> Policies { get; set; } = new();
    public List<DevicePostureDeviceGroup> DeviceGroups { get; set; } = new();
}

/// <summary>
/// UMA política observada na fonte. Guarda SÓ o necessário para a leitura operacional: identificador do registro
/// NA FONTE, nome exibido, família, rótulo de plataforma derivado do tipo declarado e o estado de atribuição.
/// NUNCA a descrição, os ajustes, os valores configurados ou qualquer payload da política.
/// </summary>
public class DevicePosturePolicy : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid DevicePostureSnapshotId { get; set; }

    /// <summary>Identificador do registro NA FONTE (referência, não correlação entre fontes distintas).</summary>
    public string ExternalId { get; set; } = "";

    public DevicePolicyKind Kind { get; set; }

    /// <summary>Nome exibido pela fonte (escolhido pelo administrador). Truncado; nunca a descrição.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Plataforma derivada DETERMINISTICAMENTE do tipo declarado pela fonte (ex.: "Windows"). Null se indeterminada.</summary>
    public string? PlatformLabel { get; set; }

    public DevicePolicyAssignmentState AssignmentState { get; set; } = DevicePolicyAssignmentState.Unknown;

    /// <summary>Quantidade de alvos de atribuição, quando a fonte devolveu a coleção. Null quando desconhecida.</summary>
    public int? AssignmentCount { get; set; }

    /// <summary>Última modificação informada PELA FONTE. Null quando ausente/inválida.</summary>
    public DateTimeOffset? SourceLastModifiedAt { get; set; }

    public DevicePostureSnapshot Snapshot { get; set; } = null!;
}

/// <summary>
/// Grupo AGREGADO de dispositivos: a interseção (sistema operacional × conformidade × criptografia × atividade)
/// com a CONTAGEM. É a única forma em que dispositivos são persistidos — não há linha por dispositivo, nenhum
/// identificador e nenhuma PII, e ainda assim os filtros da tela (conformidade, sistema operacional,
/// ativo/obsoleto, criptografia) operam sobre dados reais.
/// </summary>
public class DevicePostureDeviceGroup : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid DevicePostureSnapshotId { get; set; }

    /// <summary>Sistema operacional NORMALIZADO informado pela fonte; "Não informado" quando ausente.</summary>
    public string OperatingSystem { get; set; } = "";

    public DeviceComplianceBucket Compliance { get; set; }
    public DeviceEncryptionBucket Encryption { get; set; }
    public DeviceActivityBucket Activity { get; set; }

    public int DeviceCount { get; set; }

    public DevicePostureSnapshot Snapshot { get; set; } = null!;
}
