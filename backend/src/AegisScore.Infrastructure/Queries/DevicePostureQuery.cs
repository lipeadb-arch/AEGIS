using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Queries;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using DevicePostureSnapshot = AegisScore.Domain.DevicePostureSnapshot;

namespace AegisScore.Infrastructure.Queries;

/// <summary>
/// [AEGIS-MVP-MICROSOFT-COVERAGE-02] Autoridade ÚNICA de leitura da postura de dispositivos do tenant ambiente,
/// sobre o AegisScoreDbContext. Somente leitura, isolada pelo Global Query Filter (fail-closed): sem tenant,
/// devolve "não configurado".
///
/// A regra que atravessa toda a projeção: uma dimensão SEM inventário devolve <c>null</c> em cada número — nunca
/// zero. É o que impede a tela de afirmar "0 dispositivos não conformes" num tenant onde a permissão de
/// dispositivos sequer foi concedida. Rótulos e ações são DETERMINÍSTICOS (nunca IA) e nunca prometem avaliação
/// NIST ou alteração de score.
/// </summary>
public sealed class DevicePostureQuery : IDevicePostureQuery
{
    private const string ScoreDisclaimer =
        "As políticas e o estado de conformidade do gerenciador de dispositivos ajudam a enxergar a postura " +
        "operacional, mas não comprovam controle implementado e não alteram o AEGIS Score.";

    /// <summary>Permissão de aplicativo da dimensão de postura configurada (detalhe técnico, fora do texto executivo).</summary>
    private const string ConfigurationPermission = "DeviceManagementConfiguration.Read.All";

    /// <summary>Permissão de aplicativo da dimensão de estado efetivo dos dispositivos.</summary>
    private const string ManagedDevicesPermission = "DeviceManagementManagedDevices.Read.All";

    // [AEGIS-ENTITY-RESOLUTION-01] Sem registro por dispositivo desta fonte, a lacuna continua declarada — mas a
    // causa mudou: o identificador de diretório agora é preservado; falta a leitura por dispositivo acontecer.
    private const string CorrelationGapExplanation =
        "Ainda não há registro por dispositivo desta fonte: a vinculação aos ativos de outras fontes acontece na " +
        "próxima leitura da dimensão de dispositivos, e somente pelo identificador de dispositivo do Microsoft Entra, " +
        "no mesmo diretório. A união por nome, endereço ou semelhança não é feita — seria adivinhação, não correlação.";

    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;

    public DevicePostureQuery(AegisScoreDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task<DevicePostureViewDto> GetAsync(CancellationToken ct = default)
    {
        // Fail-closed: sem tenant ambiente, nada é projetado.
        if (_tenant.TenantId is null) return Empty(DevicePostureViewState.NotConfigured);

        // Materializa (bounded por tenant) e escolhe o mais recente em memória — evita ORDER BY de DateTimeOffset
        // no provedor (SQLite não o traduz de forma consistente), como nas demais queries desta família.
        var snapshots = await _db.DevicePostureSnapshots.AsNoTracking()
            .Include(s => s.Policies)
            .Include(s => s.DeviceGroups)
            .ToListAsync(ct);

        var snapshot = snapshots
            .OrderByDescending(s => s.ConfigurationAttemptAt ?? s.DeviceAttemptAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        if (snapshot is null)
        {
            // Sem snapshot: distingue "nunca sincronizado" (há conector) de "não configurado" (não há).
            var hasConnector = await _db.Connectors.AsNoTracking()
                .AnyAsync(c => c.Capability == ConnectorCapability.ConfigAnalyzer, ct);
            return Empty(hasConnector ? DevicePostureViewState.NeverSynced : DevicePostureViewState.NotConfigured);
        }

        var configuration = Dimension(
            snapshot.ConfigurationAttemptState, snapshot.ConfigurationState,
            snapshot.ConfigurationAttemptAt, snapshot.ConfigurationCollectedAt,
            ConfigurationPermission, ConfigurationAction);

        var assignment = Dimension(
            snapshot.AssignmentState, AssignmentStoredState(snapshot),
            snapshot.ConfigurationAttemptAt, snapshot.ConfigurationCollectedAt,
            ConfigurationPermission, AssignmentAction);

        var devices = Dimension(
            snapshot.DeviceAttemptState, snapshot.DeviceState,
            snapshot.DeviceAttemptAt, snapshot.DeviceCollectedAt,
            ManagedDevicesPermission, DeviceAction);

        var hasConfigurationData = configuration.HasData;
        var hasDeviceData = devices.HasData;
        var hasAssignmentData = assignment.HasData && hasConfigurationData;

        var configurationSummary = new DevicePostureConfigurationSummaryDto(
            CompliancePolicies: hasConfigurationData ? snapshot.CompliancePolicyCount : null,
            DeviceConfigurations: hasConfigurationData ? snapshot.DeviceConfigurationCount : null,
            TotalPolicies: hasConfigurationData
                ? snapshot.CompliancePolicyCount + snapshot.DeviceConfigurationCount
                : null,
            PoliciesAssigned: hasAssignmentData ? snapshot.PoliciesAssigned : null,
            PoliciesUnassigned: hasAssignmentData ? snapshot.PoliciesUnassigned : null,
            PoliciesAssignmentUnknown: hasConfigurationData ? snapshot.PoliciesAssignmentUnknown : null);

        var deviceSummary = new DevicePostureDeviceSummaryDto(
            TotalDevices: hasDeviceData ? snapshot.TotalDevices : null,
            Compliant: hasDeviceData ? snapshot.CompliantDevices : null,
            Noncompliant: hasDeviceData ? snapshot.NoncompliantDevices : null,
            InGracePeriod: hasDeviceData ? snapshot.InGracePeriodDevices : null,
            Conflict: hasDeviceData ? snapshot.ConflictDevices : null,
            Error: hasDeviceData ? snapshot.ErrorDevices : null,
            ManagedExternally: hasDeviceData ? snapshot.ManagedExternallyDevices : null,
            UnknownCompliance: hasDeviceData ? snapshot.UnknownComplianceDevices : null,
            Encrypted: hasDeviceData ? snapshot.EncryptedDevices : null,
            NotEncrypted: hasDeviceData ? snapshot.NotEncryptedDevices : null,
            UnknownEncryption: hasDeviceData ? snapshot.UnknownEncryptionDevices : null,
            Active: hasDeviceData ? snapshot.ActiveDevices : null,
            Stale: hasDeviceData ? snapshot.StaleDevices : null,
            UnknownActivity: hasDeviceData ? snapshot.UnknownActivityDevices : null,
            StaleThresholdDays: snapshot.StaleThresholdDays > 0 ? snapshot.StaleThresholdDays : 30);

        // Sem inventário na dimensão, a lista correspondente vem VAZIA — e a tela lê o estado, não a lista, para
        // decidir o que dizer (lista vazia jamais significa "nenhuma política"/"nenhum dispositivo").
        var policies = hasConfigurationData
            ? snapshot.Policies
                .Select(ToPolicyDto)
                .OrderBy(p => p.Kind, StringComparer.Ordinal)
                .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<DevicePolicyDto>();

        var deviceGroups = hasDeviceData
            ? snapshot.DeviceGroups
                .Select(ToGroupDto)
                .OrderByDescending(g => g.DeviceCount)
                .ThenBy(g => g.OperatingSystem, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.Compliance, StringComparer.Ordinal)
                .ToList()
            : new List<DeviceGroupDto>();

        var state = hasConfigurationData || hasDeviceData
            ? DevicePostureViewState.Data
            : DevicePostureViewState.NeverSynced;

        var correlation = await CorrelationAsync(
            snapshot.ConnectorConfigId, hasDeviceData ? snapshot.DevicesWithDirectoryId : null, ct);

        return new DevicePostureViewDto(
            State: state,
            Source: snapshot.Source,
            Configuration: configuration,
            Assignment: assignment,
            Devices: devices,
            ConfigurationSummary: configurationSummary,
            DeviceSummary: deviceSummary,
            Policies: policies,
            DeviceGroups: deviceGroups,
            Correlation: correlation,
            AffectsScore: false,
            ScoreDisclaimer: ScoreDisclaimer);
    }

    /// <summary>
    /// [AEGIS-ENTITY-RESOLUTION-01] Estado REAL da vinculação dos dispositivos desta fonte, a partir dos registros por
    /// dispositivo presentes na última leitura. Três grupos que não se confundem: vinculado a OUTRA fonte pelo
    /// identificador de diretório; sem evidência para vincular; em conflito preservado.
    /// </summary>
    private async Task<DevicePostureCorrelationDto> CorrelationAsync(
        Guid connectorId, int? devicesWithDirectoryId, CancellationToken ct)
    {
        var active = _db.AssetSourceBindings.AsNoTracking()
            .Where(b => b.ConnectorConfigId == connectorId && b.IsActive);
        var observed = await active.CountAsync(ct);
        if (observed == 0)
            return new DevicePostureCorrelationDto(false, devicesWithDirectoryId, CorrelationGapExplanation);

        var linkedAcross = await active
            .Where(b => b.ResolutionState == AssetBindingResolutionState.Linked)
            .CountAsync(b => _db.AssetSourceBindings.Any(o =>
                o.AssetId == b.AssetId && o.ConnectorConfigId != connectorId
                && o.IsActive && o.ResolutionState == AssetBindingResolutionState.Linked), ct);
        var withoutLink = await active.CountAsync(b =>
            b.ResolutionState == AssetBindingResolutionState.NoIdentifier
            || b.ResolutionState == AssetBindingResolutionState.InvalidIdentifier
            || b.ResolutionState == AssetBindingResolutionState.DirectoryUnconfirmed, ct);
        var conflicts = await active.CountAsync(b => b.ResolutionState == AssetBindingResolutionState.Conflict, ct);

        var explanation =
            $"{observed} dispositivo(s) desta fonte têm registro por dispositivo: {linkedAcross} vinculado(s) a " +
            "registro de outra fonte pelo identificador de dispositivo do Microsoft Entra, no mesmo diretório; " +
            $"{withoutLink} sem identificador válido para vínculo (o que não significa que sejam outros dispositivos); " +
            $"{conflicts} com conflito preservado para análise. Nome, endereço ou semelhança nunca unem ativos, e o " +
            "vínculo confirma a associação dos registros — não a segurança do dispositivo.";

        return new DevicePostureCorrelationDto(
            DeterministicCorrelationAvailable: true,
            DevicesWithDirectoryId: devicesWithDirectoryId,
            Explanation: explanation,
            DevicesObserved: observed,
            DevicesLinkedAcrossSources: linkedAcross,
            DevicesWithoutLink: withoutLink,
            DevicesInConflict: conflicts);
    }

    // ---- Projeção de dimensões ------------------------------------------------------------------------

    /// <summary>
    /// A sub-dimensão de ATRIBUIÇÃO só tem "dados armazenados" quando o estado persistido dela é utilizável — o
    /// mesmo campo guarda tanto o desfecho quanto a completude, porque a atribuição não é preservada
    /// separadamente das políticas que a carregam.
    /// </summary>
    private static DevicePostureDimensionState AssignmentStoredState(DevicePostureSnapshot s) =>
        s.AssignmentState is DevicePostureDimensionState.Available or DevicePostureDimensionState.Partial
            ? s.AssignmentState
            : DevicePostureDimensionState.NeverCollected;

    private static DevicePostureDimensionDto Dimension(
        DevicePostureDimensionState attemptState,
        DevicePostureDimensionState storedState,
        DateTimeOffset? attemptAt,
        DateTimeOffset? collectedAt,
        string permission,
        Func<DevicePostureDimensionState, string?> action)
    {
        var hasData = storedState is DevicePostureDimensionState.Available or DevicePostureDimensionState.Partial;
        var isStale = hasData
            && attemptState is not (DevicePostureDimensionState.Available or DevicePostureDimensionState.Partial);

        return new DevicePostureDimensionDto(
            State: attemptState.ToString(),
            StoredState: hasData ? storedState.ToString() : null,
            Label: LabelOf(attemptState),
            HasData: hasData,
            IsStale: isStale,
            RequiredPermission: permission,
            ActionHint: action(attemptState),
            LastAttemptAt: attemptAt,
            LastCollectionAt: hasData ? collectedAt : null);
    }

    /// <summary>Rótulo pt-BR determinístico. Nenhum estado de falha compartilha rótulo com um estado de sucesso.</summary>
    internal static string LabelOf(DevicePostureDimensionState state) => state switch
    {
        DevicePostureDimensionState.Available => "Disponível",
        DevicePostureDimensionState.Partial => "Parcial",
        DevicePostureDimensionState.NotAuthorized => "Bloqueada por permissão",
        DevicePostureDimensionState.NotLicensed => "Indisponível por licença",
        DevicePostureDimensionState.Unavailable => "Indisponível",
        _ => "Nunca coletada",
    };

    private static string? ConfigurationAction(DevicePostureDimensionState state) => state switch
    {
        DevicePostureDimensionState.NotAuthorized =>
            $"Conceda a permissão de aplicativo {ConfigurationPermission} e consinta no tenant Microsoft.",
        DevicePostureDimensionState.NotLicensed =>
            "Verifique se o tenant tem o gerenciador de dispositivos licenciado e provisionado.",
        DevicePostureDimensionState.Unavailable =>
            "Tente sincronizar novamente; se persistir, verifique a conectividade com o Microsoft Graph.",
        DevicePostureDimensionState.Partial =>
            "Sincronize novamente para completar a leitura das políticas.",
        _ => null,
    };

    private static string? AssignmentAction(DevicePostureDimensionState state) => state switch
    {
        DevicePostureDimensionState.Unavailable or DevicePostureDimensionState.Partial =>
            "A fonte não devolveu as atribuições de todas as políticas; o alcance de parte delas não pode ser afirmado.",
        _ => null,
    };

    private static string? DeviceAction(DevicePostureDimensionState state) => state switch
    {
        DevicePostureDimensionState.NotAuthorized =>
            $"Conceda a permissão de aplicativo {ManagedDevicesPermission} e consinta no tenant Microsoft para " +
            "enxergar o estado efetivo dos dispositivos.",
        DevicePostureDimensionState.NotLicensed =>
            "Verifique se o tenant tem o gerenciador de dispositivos licenciado e provisionado.",
        DevicePostureDimensionState.Unavailable =>
            "Tente sincronizar novamente; se persistir, verifique a conectividade com o Microsoft Graph.",
        DevicePostureDimensionState.Partial =>
            "Sincronize novamente para completar a leitura dos dispositivos.",
        _ => null,
    };

    // ---- Projeção de filhos ----------------------------------------------------------------------------

    private static DevicePolicyDto ToPolicyDto(DevicePosturePolicy p) => new(
        ExternalId: p.ExternalId,
        Kind: p.Kind.ToString(),
        KindLabel: p.Kind == DevicePolicyKind.CompliancePolicy
            ? "Política de conformidade"
            : "Configuração de dispositivo",
        DisplayName: p.DisplayName,
        PlatformLabel: p.PlatformLabel,
        AssignmentState: p.AssignmentState.ToString(),
        AssignmentLabel: p.AssignmentState switch
        {
            DevicePolicyAssignmentState.Assigned => "Atribuída",
            DevicePolicyAssignmentState.Unassigned => "Sem atribuição",
            _ => "Atribuição desconhecida",
        },
        AssignmentCount: p.AssignmentCount,
        LastModifiedAt: p.SourceLastModifiedAt);

    private static DeviceGroupDto ToGroupDto(DevicePostureDeviceGroup g) => new(
        OperatingSystem: g.OperatingSystem,
        Compliance: g.Compliance.ToString(),
        ComplianceLabel: g.Compliance switch
        {
            DeviceComplianceBucket.Compliant => "Conforme",
            DeviceComplianceBucket.Noncompliant => "Não conforme",
            DeviceComplianceBucket.InGracePeriod => "Em período de carência",
            DeviceComplianceBucket.Conflict => "Políticas em conflito",
            DeviceComplianceBucket.Error => "Erro na avaliação",
            DeviceComplianceBucket.ManagedExternally => "Avaliado por gerenciador externo",
            _ => "Não avaliado",
        },
        Encryption: g.Encryption.ToString(),
        EncryptionLabel: g.Encryption switch
        {
            DeviceEncryptionBucket.Encrypted => "Criptografado",
            DeviceEncryptionBucket.NotEncrypted => "Sem criptografia",
            _ => "Criptografia não informada",
        },
        Activity: g.Activity.ToString(),
        ActivityLabel: g.Activity switch
        {
            DeviceActivityBucket.Active => "Sincronizado recentemente",
            DeviceActivityBucket.Stale => "Sem sincronização recente",
            _ => "Sincronização não informada",
        },
        DeviceCount: g.DeviceCount);

    // ---- Vazio honesto ----------------------------------------------------------------------------------

    private static DevicePostureViewDto Empty(DevicePostureViewState state)
    {
        var never = new DevicePostureDimensionDto(
            State: DevicePostureDimensionState.NeverCollected.ToString(),
            StoredState: null,
            Label: LabelOf(DevicePostureDimensionState.NeverCollected),
            HasData: false,
            IsStale: false,
            RequiredPermission: null,
            ActionHint: null,
            LastAttemptAt: null,
            LastCollectionAt: null);

        return new DevicePostureViewDto(
            State: state,
            Source: null,
            Configuration: never with { RequiredPermission = ConfigurationPermission },
            Assignment: never with { RequiredPermission = ConfigurationPermission },
            Devices: never with { RequiredPermission = ManagedDevicesPermission },
            ConfigurationSummary: new DevicePostureConfigurationSummaryDto(null, null, null, null, null, null),
            DeviceSummary: new DevicePostureDeviceSummaryDto(
                null, null, null, null, null, null, null, null, null, null, null, null, null, null, 30),
            Policies: Array.Empty<DevicePolicyDto>(),
            DeviceGroups: Array.Empty<DeviceGroupDto>(),
            Correlation: new DevicePostureCorrelationDto(false, null, CorrelationGapExplanation),
            AffectsScore: false,
            ScoreDisclaimer: ScoreDisclaimer);
    }
}
