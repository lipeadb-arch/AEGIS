using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AegisScore.Application.Queries;

/// <summary>
/// [AEGIS-MVP-MICROSOFT-COVERAGE-02] Autoridade ÚNICA de leitura, tenant-scoped e SOMENTE LEITURA, da postura de
/// configuração e conformidade de dispositivos. Tenant IMPLÍCITO (fail-closed via ITenantContext + Global Query
/// Filter). Devolve SÓ agregados seguros — nunca configuração da integração, credencial, identificador/nome de
/// dispositivo, usuário, payload de política ou dados de outro tenant.
/// </summary>
public interface IDevicePostureQuery
{
    Task<DevicePostureViewDto> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// Estado GERAL da visão — o frontend escolhe a tela por ele. <c>NotConfigured</c> = sem conector de gestão de
/// dispositivos; <c>NeverSynced</c> = conector existe mas nenhuma dimensão foi coletada; <c>Data</c> = há ao menos
/// UMA dimensão com inventário (a completude de CADA dimensão vive na própria dimensão, nunca aqui).
/// </summary>
public enum DevicePostureViewState { NotConfigured = 0, NeverSynced = 1, Data = 2 }

/// <summary>
/// Contrato de leitura da postura de dispositivos. <see cref="AffectsScore"/> é SEMPRE <c>false</c> e
/// <see cref="ScoreDisclaimer"/> carrega o aviso explícito: políticas configuradas e dispositivos conformes NÃO
/// alteram o AEGIS Score nem comprovam conformidade NIST.
///
/// Regra de honestidade que atravessa TODO o contrato: os números de uma dimensão SEM inventário são
/// <c>null</c> — nunca zero. "0 dispositivos não conformes" só existe quando a dimensão foi de fato coletada.
/// </summary>
public sealed record DevicePostureViewDto(
    DevicePostureViewState State,
    string? Source,
    DevicePostureDimensionDto Configuration,
    DevicePostureDimensionDto Assignment,
    DevicePostureDimensionDto Devices,
    DevicePostureConfigurationSummaryDto ConfigurationSummary,
    DevicePostureDeviceSummaryDto DeviceSummary,
    IReadOnlyList<DevicePolicyDto> Policies,
    IReadOnlyList<DeviceGroupDto> DeviceGroups,
    DevicePostureCorrelationDto Correlation,
    bool AffectsScore,
    string ScoreDisclaimer);

/// <summary>
/// Estado de UMA dimensão (ou sub-dimensão) em linguagem executiva. <see cref="HasData"/> é o que autoriza a tela
/// a mostrar números; <see cref="StoredState"/> distingue "há inventário preservado" de "a última tentativa
/// falhou". <see cref="ActionHint"/> é a ação OBJETIVA para destravar — nunca um erro genérico.
/// </summary>
public sealed record DevicePostureDimensionDto(
    /// <summary>Desfecho da última TENTATIVA: "Available"|"Partial"|"NotAuthorized"|"NotLicensed"|"Unavailable"|"NeverCollected".</summary>
    string State,
    /// <summary>Estado dos DADOS armazenados ("Available"|"Partial") ou null quando nunca houve inventário.</summary>
    string? StoredState,
    /// <summary>Rótulo pt-BR determinístico do estado da última tentativa.</summary>
    string Label,
    bool HasData,
    /// <summary>Verdadeiro quando há inventário preservado mas a última tentativa falhou (números podem estar defasados).</summary>
    bool IsStale,
    /// <summary>Permissão de aplicativo que esta dimensão exige (detalhe técnico, exibido fora do texto executivo).</summary>
    string? RequiredPermission,
    /// <summary>Ação objetiva para completar a integração, quando aplicável.</summary>
    string? ActionHint,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastCollectionAt);

/// <summary>Totais da POSTURA CONFIGURADA. Todos <c>null</c> quando a dimensão não tem inventário.</summary>
public sealed record DevicePostureConfigurationSummaryDto(
    int? CompliancePolicies,
    int? DeviceConfigurations,
    int? TotalPolicies,
    /// <summary>Políticas com ao menos um alvo de atribuição. Null quando a atribuição não é comprovável.</summary>
    int? PoliciesAssigned,
    /// <summary>Políticas com coleção de atribuições VAZIA (comprovadamente sem alcance). Null quando não comprovável.</summary>
    int? PoliciesUnassigned,
    /// <summary>Políticas cuja atribuição a fonte não permitiu afirmar — nunca somadas às "não atribuídas".</summary>
    int? PoliciesAssignmentUnknown);

/// <summary>
/// Totais do ESTADO EFETIVO dos dispositivos. Todos <c>null</c> quando a dimensão não tem inventário — é o que
/// impede a tela de dizer "0 dispositivos não conformes" para um tenant cuja permissão nem foi concedida.
/// </summary>
public sealed record DevicePostureDeviceSummaryDto(
    int? TotalDevices,
    int? Compliant,
    int? Noncompliant,
    int? InGracePeriod,
    int? Conflict,
    int? Error,
    int? ManagedExternally,
    int? UnknownCompliance,
    int? Encrypted,
    int? NotEncrypted,
    int? UnknownEncryption,
    int? Active,
    int? Stale,
    int? UnknownActivity,
    /// <summary>Janela (dias) a partir da qual um dispositivo conta como sem sincronização recente.</summary>
    int StaleThresholdDays);

/// <summary>Uma política, em linguagem executiva. Nunca descrição, ajustes ou valores configurados.</summary>
public sealed record DevicePolicyDto(
    string ExternalId,
    /// <summary>"CompliancePolicy" | "DeviceConfiguration".</summary>
    string Kind,
    /// <summary>Rótulo pt-BR da família ("Política de conformidade" | "Configuração de dispositivo").</summary>
    string KindLabel,
    string DisplayName,
    string? PlatformLabel,
    /// <summary>"Assigned" | "Unassigned" | "Unknown".</summary>
    string AssignmentState,
    string AssignmentLabel,
    int? AssignmentCount,
    DateTimeOffset? LastModifiedAt);

/// <summary>
/// Um recorte AGREGADO de dispositivos (SO × conformidade × criptografia × atividade). É o grão sobre o qual os
/// filtros da tela operam — sem nenhum identificador de dispositivo, nome, usuário ou PII.
/// </summary>
public sealed record DeviceGroupDto(
    string OperatingSystem,
    /// <summary>"Compliant"|"Noncompliant"|"InGracePeriod"|"Conflict"|"Error"|"ManagedExternally"|"Unknown".</summary>
    string Compliance,
    string ComplianceLabel,
    /// <summary>"Encrypted"|"NotEncrypted"|"Unknown".</summary>
    string Encryption,
    string EncryptionLabel,
    /// <summary>"Active"|"Stale"|"Unknown".</summary>
    string Activity,
    string ActivityLabel,
    int DeviceCount);

/// <summary>
/// Estado da CORRELAÇÃO com os ativos já inventariados por outras fontes. Enquanto não houver um identificador
/// estável preservado dos DOIS lados, a correlação determinística é impossível — e o AEGIS registra a lacuna em
/// vez de unir ativos por nome, IP ou heurística.
/// </summary>
public sealed record DevicePostureCorrelationDto(
    bool DeterministicCorrelationAvailable,
    /// <summary>Dispositivos que trazem um id de dispositivo de diretório na fonte (o valor NÃO é preservado).</summary>
    int? DevicesWithDirectoryId,
    string Explanation);
