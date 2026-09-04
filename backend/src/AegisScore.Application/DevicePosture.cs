using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Abstractions;

// ---- [AEGIS-MVP-MICROSOFT-COVERAGE-02] Postura de dispositivos — contrato PROVIDER-NEUTRAL ----
// Mesmo idioma de DetectionCoverageSnapshot/SoftwareInventoryCollection: o ADAPTADOR normaliza a resposta da
// fonte no vocabulário do AEGIS; o EvidenceIngestionExecutor reconcilia (o adaptador NUNCA escreve no banco).
// Nomes provider-neutral (nada de "Intune"/"Microsoft" aqui): o rótulo da fonte vive em Source.
//
// É EVIDÊNCIA OPERACIONAL CONSULTIVA: não gera EvidenceSignal, não mapeia NIST, não gera pontos.

/// <summary>
/// UMA política observada, no vocabulário do AEGIS. <see cref="ExternalId"/> é a referência do registro NA FONTE
/// (nunca uma chave de correlação entre fontes distintas). Nunca descrição, ajustes ou valores configurados.
/// </summary>
public sealed record DevicePolicyFact(
    string ExternalId,
    DevicePolicyKind Kind,
    string DisplayName,
    string? PlatformLabel,
    DevicePolicyAssignmentState AssignmentState,
    int? AssignmentCount,
    DateTimeOffset? SourceLastModifiedAt);

/// <summary>
/// Dimensão 1 — POSTURA CONFIGURADA. <see cref="State"/> é EXPLÍCITO: uma falha classificável (permissão,
/// licença, indisponibilidade, teto) vira estado, nunca exceção e nunca coleção vazia.
/// <see cref="AssignmentState"/> é uma SUB-dimensão independente: as políticas podem estar disponíveis com a
/// atribuição não comprovável pelo contrato oficial da fonte.
/// </summary>
public sealed record DevicePostureConfigurationDimension(
    DevicePostureDimensionState State,
    DateTimeOffset AttemptedAt,
    IReadOnlyList<DevicePolicyFact> Policies,
    DevicePostureDimensionState AssignmentState,
    int InvalidPolicies,
    /// <summary>Detalhe SANITIZADO da tentativa (ex.: menção a DeviceManagementConfiguration.Read.All). Nunca token/URL/payload.</summary>
    string? Detail = null)
{
    public bool IsComplete => State == DevicePostureDimensionState.Available;

    /// <summary>Houve inventário utilizável (completo ou piso parcial) — distingue de falha/nunca coletado.</summary>
    public bool HasInventory =>
        State is DevicePostureDimensionState.Available or DevicePostureDimensionState.Partial;

    public static DevicePostureConfigurationDimension Failed(
        DevicePostureDimensionState state, DateTimeOffset attemptedAt, string? detail) =>
        new(state, attemptedAt, Array.Empty<DevicePolicyFact>(),
            DevicePostureDimensionState.NeverCollected, 0, detail);
}

/// <summary>
/// Grupo AGREGADO de dispositivos (SO × conformidade × criptografia × atividade) com a contagem. É o ÚNICO
/// grão em que dispositivos trafegam: nenhum identificador de dispositivo, nenhum nome, nenhuma PII.
/// </summary>
public sealed record DeviceGroupFact(
    string OperatingSystem,
    DeviceComplianceBucket Compliance,
    DeviceEncryptionBucket Encryption,
    DeviceActivityBucket Activity,
    int DeviceCount);

/// <summary>
/// Dimensão 2 — ESTADO EFETIVO DOS DISPOSITIVOS. Independente da dimensão 1: sem a permissão de dispositivos
/// esta dimensão fica <see cref="DevicePostureDimensionState.NotAuthorized"/> e a de políticas segue utilizável.
/// <see cref="TotalDevices"/> é a soma das contagens dos grupos — nunca um número paralelo.
/// </summary>
public sealed record DevicePostureDeviceDimension(
    DevicePostureDimensionState State,
    DateTimeOffset AttemptedAt,
    IReadOnlyList<DeviceGroupFact> Groups,
    int TotalDevices,
    /// <summary>Janela (dias) usada para classificar um dispositivo como obsoleto por falta de sincronização.</summary>
    int StaleThresholdDays,
    /// <summary>Dispositivos que trazem um id de dispositivo de diretório na fonte — FATO de correlação, sem o valor.</summary>
    int DevicesWithDirectoryId,
    int InvalidDevices,
    /// <summary>Detalhe SANITIZADO (ex.: menção a DeviceManagementManagedDevices.Read.All). Nunca token/URL/payload.</summary>
    string? Detail = null)
{
    public bool IsComplete => State == DevicePostureDimensionState.Available;

    public bool HasInventory =>
        State is DevicePostureDimensionState.Available or DevicePostureDimensionState.Partial;

    public static DevicePostureDeviceDimension Failed(
        DevicePostureDimensionState state, DateTimeOffset attemptedAt, string? detail, int staleThresholdDays) =>
        new(state, attemptedAt, Array.Empty<DeviceGroupFact>(), 0, staleThresholdDays, 0, 0, detail);
}

/// <summary>
/// Fotografia NORMALIZADA, SEGURA e PROVIDER-NEUTRAL da postura de dispositivos: as DUAS dimensões, cada uma
/// com seu estado. É um FATO OPERACIONAL consultivo — NÃO vira EvidenceSignal, NÃO alimenta o AEGIS Score, NÃO
/// altera conformidade NIST nem os estados determinísticos dos controles.
///
/// A existência de uma política NÃO comprova controle implementado, política atribuída, política eficaz nem
/// conformidade; um dispositivo "conforme" segundo a fonte NÃO é prova suficiente de um controle NIST.
/// </summary>
public sealed record DevicePostureSnapshot(
    string Source,
    DevicePostureConfigurationDimension Configuration,
    DevicePostureDeviceDimension Devices)
{
    /// <summary>Só uma sincronização com AS DUAS dimensões completas é "plenamente operacional".</summary>
    public bool IsComplete => Configuration.IsComplete && Devices.IsComplete;

    /// <summary>Ao menos uma dimensão produziu inventário utilizável.</summary>
    public bool HasAnyInventory => Configuration.HasInventory || Devices.HasInventory;
}

/// <summary>
/// [AEGIS-MVP-MICROSOFT-COVERAGE-02] Capacidade COMPLEMENTAR a <see cref="IEvidenceConnector"/>: um conector que
/// produz a POSTURA DE DISPOSITIVOS (configuração + estado efetivo) por leitura administrativa somente-leitura.
/// O executor de pull a detecta no MESMO fluxo, como dimensão INDEPENDENTE — SEM criar EvidenceSignal, SEM
/// mapear NIST e SEM tocar o score.
///
/// Como <see cref="IDetectionCoverageCollector"/>, esta coleta NÃO lança em falha da fonte: devolve SEMPRE uma
/// fotografia com os estados classificados (cada dimensão separadamente). Só o cancelamento SOLICITADO propaga.
/// </summary>
public interface IDevicePostureCollector
{
    ConnectorProvider Provider { get; }
    ConnectorCapability Capability { get; }

    /// <summary>Coleta a postura de dispositivos (só leitura). Falha da fonte é SANITIZADA e vira estado, não exceção.</summary>
    Task<DevicePostureSnapshot> CollectDevicePostureAsync(ConnectorConfig config, CancellationToken ct);
}

/// <summary>
/// Contagens HONESTAS de uma reconciliação de postura de dispositivos (superfície do resultado de sincronização).
/// Só estados e números — nunca política/dispositivo concretos. Aditivo a <see cref="PullIngestionResult"/>.
///
/// Os três estados são os da TENTATIVA que acabou de ocorrer (não os dos dados preservados): uma dimensão que
/// falhou aparece como falha aqui, mesmo com um inventário anterior intacto no banco. <see cref="TotalDevices"/>
/// é o total ARMAZENADO — a borda só o expõe quando a tentativa produziu inventário, nunca como zero.
/// </summary>
public sealed record DevicePostureSyncResult(
    DevicePostureDimensionState ConfigurationState,
    DevicePostureDimensionState AssignmentState,
    DevicePostureDimensionState DeviceState,
    int PoliciesStored,
    int DeviceGroupsStored,
    int TotalDevices,
    bool ConfigurationPreserved,
    bool DevicesPreserved);
