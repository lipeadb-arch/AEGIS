using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Domain;
using Microsoft.Extensions.Logging;
using static AegisScore.Connectors.Microsoft.Knight.Services.KnightJson;

namespace AegisScore.Connectors.Microsoft.Knight.Services;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-04] Coletores de configuração por API REST: Intune, SharePoint e Fabric
// ============================================================================
// Os três seguem o caminho já firmado pelo Teams e pelo Exchange — coleta → documentos tipados → ADM → releitura →
// avaliação —, com uma capacidade por leitura e o desfecho próprio de cada uma. Nenhum grava nada na fonte: só GET.

/// <summary>Base comum: resolve a configuração, obtém o token e devolve falha declarada quando não obtém.</summary>
public abstract class KnightRestCollectorBase : IKnightCollector
{
    protected readonly ILogger? Log;
    protected readonly TimeProvider Time;

    protected KnightRestCollectorBase(ILogger? log, TimeProvider? time)
    {
        Log = log;
        Time = time ?? TimeProvider.System;
    }

    public abstract KnightSourceType Source { get; }

    protected string Label => KnightSourceCatalog.Label(Source);

    /// <summary>Capacidades que este coletor tenta em cada coleta, na ordem de execução.</summary>
    public abstract IReadOnlyList<KnightCapability> Capabilities { get; }

    public async Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Configuration is not IMicrosoftGraphCredentials credentials || context.Configuration.Source != Source)
            return KnightCollectionResult.NotConfigured(Source, Label);

        var recorder = new KnightCapabilityRecorder(Label, Log);
        try
        {
            await CollectCoreAsync(credentials, context.Configuration, recorder, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (EntraGraphException ex)
        {
            // Falha ANTES das leituras (token): nenhuma capacidade foi tentada, e todas carregam o mesmo motivo.
            var state = KnightCapabilityRecorder.StateFor(ex.Kind);
            var (_, reason) = KnightCapabilityRecorder.Classify(ex, Label);
            Log?.LogWarning("Falha ao obter o token de aplicativo para {Source}: Kind={Kind}, HttpStatus={Status}.", Source, ex.Kind, ex.HttpStatusCode);
            var remaining = Capabilities.Where(c => recorder.Capabilities.All(r => r.Capability != c));
            var failure = KnightCapabilityRecorder.Failure(Source, Label, remaining, state, KnightCapabilityRecorder.OutcomeFor(state),
                reason, Time.GetUtcNow());
            if (recorder.Capabilities.Count == 0) return failure;
            recorder.Capabilities.AddRange(failure.Capabilities);
        }

        return recorder.Result(Source, Label, Time.GetUtcNow());
    }

    protected abstract Task CollectCoreAsync(
        IMicrosoftGraphCredentials credentials, KnightSourceConfiguration configuration, KnightCapabilityRecorder recorder, CancellationToken ct);
}

// ---- Microsoft Intune ------------------------------------------------------------------------------------

/// <summary>
/// Configurações do Intune pelo Microsoft Graph v1.0: o comportamento com dispositivos SEM política de conformidade
/// (<c>deviceManagement.settings.secureByDefault</c>, <c>DeviceManagementConfiguration.Read.All</c>) e as restrições
/// de registro por plataforma (<c>deviceEnrollmentConfigurations</c>, <c>DeviceManagementServiceConfig.Read.All</c>).
/// </summary>
public sealed class IntuneKnightCollector : KnightRestCollectorBase
{
    internal const string SettingsUrl = "deviceManagement?$select=settings";
    internal const string EnrollmentUrl = "deviceManagement/deviceEnrollmentConfigurations?$expand=assignments";

    /// <summary>Tipo documentado da configuração de restrições de plataforma na v1.0.</summary>
    internal const string PlatformRestrictionsType = "#microsoft.graph.deviceEnrollmentPlatformRestrictionsConfiguration";

    /// <summary>Plataformas da configuração e o nome do AEGIS para cada uma.</summary>
    internal static readonly (string Property, string Platform)[] PlatformProperties =
    {
        ("windowsRestriction", "Windows"),
        ("iosRestriction", "iOS/iPadOS"),
        ("androidRestriction", "Android (administrador do dispositivo)"),
        ("androidForWorkRestriction", "Android Enterprise (perfil de trabalho)"),
        ("macOSRestriction", "macOS"),
    };

    private readonly IEntraGraphClient _graph;

    public IntuneKnightCollector(IEntraGraphClient graph, ILogger<IntuneKnightCollector>? log = null, TimeProvider? time = null)
        : base(log, time) => _graph = graph;

    public override KnightSourceType Source => KnightSourceType.MicrosoftIntune;

    public override IReadOnlyList<KnightCapability> Capabilities { get; } = new[]
    {
        KnightCapability.IntuneServiceSettings,
        KnightCapability.IntuneEnrollmentRestrictions,
    };

    protected override async Task CollectCoreAsync(
        IMicrosoftGraphCredentials cfg, KnightSourceConfiguration _, KnightCapabilityRecorder recorder, CancellationToken ct)
    {
        var token = await _graph.AcquireTokenAsync(cfg, ct);

        await recorder.RunAsync(KnightCapability.IntuneServiceSettings, async add =>
        {
            var root = await _graph.GetJsonAsync(token, cfg, SettingsUrl, ct);
            var settings = Prop(root, "settings");
            add(KnightTenantConfiguration.Document(IntuneComplianceSettings.ExternalId, "Configurações de conformidade do Intune",
                new IntuneComplianceSettings(Bool(settings, "secureByDefault"), Int(settings, "deviceComplianceCheckinThresholdDays"))));
        }, ct);

        await recorder.RunAsync(KnightCapability.IntuneEnrollmentRestrictions, async add =>
        {
            await foreach (var c in _graph.GetPagedAsync(token, cfg, EnrollmentUrl, ct))
            {
                if (!string.Equals(Str(c, "@odata.type"), PlatformRestrictionsType, StringComparison.OrdinalIgnoreCase)) continue;
                var id = Str(c, "id");
                if (id is null) continue;
                var priority = Int(c, "priority");
                var platforms = PlatformProperties
                    .Select(p => (p.Platform, Value: Prop(c, p.Property)))
                    .Where(p => p.Value.ValueKind == JsonValueKind.Object)
                    .Select(p => new IntunePlatformRestriction(p.Platform, Bool(p.Value, "platformBlocked"), Bool(p.Value, "personalDeviceEnrollmentBlocked")))
                    .ToList();
                var isDefault = priority == 0 || id.EndsWith("_DefaultPlatformRestrictions", StringComparison.OrdinalIgnoreCase);
                add(KnightTenantConfiguration.Document(id, Str(c, "displayName"),
                    new IntuneEnrollmentRestriction(id, Str(c, "displayName"), priority, isDefault, platforms, Items(c, "assignments").Count())));
            }
        }, ct);
    }
}

// ---- SharePoint e OneDrive -------------------------------------------------------------------------------

/// <summary>
/// SharePoint e OneDrive em DUAS leituras independentes:
///   • Microsoft Graph v1.0 <c>GET /admin/sharepoint/settings</c> (<c>SharePointTenantSettings.Read.All</c>, aceita
///     segredo) — compartilhamento externo, repartilhamento por convidados, domínios, autenticação legada e
///     sincronização em dispositivos não gerenciados;
///   • API de administração do SharePoint (objeto <c>Tenant</c>), a ÚNICA leitura oficial das demais configurações
///     (OneDrive, links padrão, expiração de convidados, reautenticação, download de arquivo infectado, B2B). A
///     Microsoft exige, sem usuário, <c>Sites.FullControl.All</c> no SharePoint e token obtido por CERTIFICADO — sem
///     certificado configurado a leitura não é tentada e o motivo é registrado.
/// </summary>
public sealed class SharePointKnightCollector : KnightRestCollectorBase
{
    internal const string GraphSettingsUrl = "admin/sharepoint/settings";
    internal const string OrganizationUrl = "organization?$select=verifiedDomains";

    internal const string CertificateRequired =
        "A API de administração do SharePoint só aceita token de aplicativo obtido por certificado; o conector Microsoft não "
        + "tem certificado configurado. Configurações de OneDrive, links, expiração de convidados, reautenticação, arquivos "
        + "infectados e integração B2B não foram lidas.";

    private readonly IEntraGraphClient _graph;
    private readonly IMicrosoftAppTokenClient _tokens;
    private readonly IMicrosoftRestClient _rest;

    public SharePointKnightCollector(IEntraGraphClient graph, IMicrosoftAppTokenClient tokens, IMicrosoftRestClient rest,
        ILogger<SharePointKnightCollector>? log = null, TimeProvider? time = null) : base(log, time)
    {
        _graph = graph;
        _tokens = tokens;
        _rest = rest;
    }

    public override KnightSourceType Source => KnightSourceType.MicrosoftSharePoint;

    public override IReadOnlyList<KnightCapability> Capabilities { get; } = new[]
    {
        KnightCapability.SharePointTenantSettings,
        KnightCapability.SharePointAdminTenant,
    };

    /// <summary>Host de administração derivado do domínio inicial (<c>contoso.onmicrosoft.com</c> → <c>contoso-admin.sharepoint.com</c>).</summary>
    public static string? AdminHost(string? initialDomain)
    {
        if (string.IsNullOrWhiteSpace(initialDomain)) return null;
        var d = initialDomain.Trim().ToLowerInvariant();
        const string suffix = ".onmicrosoft.com";
        if (!d.EndsWith(suffix, StringComparison.Ordinal)) return null;
        var prefix = d[..^suffix.Length];
        return prefix.Length > 0 && prefix.All(ch => char.IsLetterOrDigit(ch) || ch == '-') ? $"{prefix}-admin.sharepoint.com" : null;
    }

    protected override async Task CollectCoreAsync(
        IMicrosoftGraphCredentials cfg, KnightSourceConfiguration _, KnightCapabilityRecorder recorder, CancellationToken ct)
    {
        var token = await _graph.AcquireTokenAsync(cfg, ct);

        await recorder.RunAsync(KnightCapability.SharePointTenantSettings, async add =>
        {
            var s = await _graph.GetJsonAsync(token, cfg, GraphSettingsUrl, ct);
            add(KnightTenantConfiguration.Document(SharePointTenantSettings.ExternalId, "Configurações do SharePoint e OneDrive (Graph)",
                new SharePointTenantSettings(
                    Str(s, "sharingCapability"), Str(s, "sharingDomainRestrictionMode"),
                    Strings(s, "sharingAllowedDomainList"), Strings(s, "sharingBlockedDomainList"),
                    Bool(s, "isResharingByExternalUsersEnabled"), Bool(s, "isLegacyAuthProtocolsEnabled"),
                    Bool(s, "isUnmanagedSyncAppForTenantRestricted"), Strings(s, "allowedDomainGuidsForSyncApp").Count,
                    Bool(s, "isRequireAcceptingUserToMatchInvitedUserEnabled"))));
        }, ct);

        if (cfg.ClientCertificate is null)
        {
            recorder.Record(KnightCapability.SharePointAdminTenant, KnightCapabilityOutcome.NotAttempted, CertificateRequired);
            return;
        }

        await recorder.RunAsync(KnightCapability.SharePointAdminTenant, async add =>
        {
            var org = await _graph.GetJsonAsync(token, cfg, OrganizationUrl, ct);
            var initial = Items(org, "value").SelectMany(o => Items(o, "verifiedDomains"))
                .FirstOrDefault(d => Bool(d, "isInitial") == true);
            var host = AdminHost(initial.ValueKind == JsonValueKind.Object ? Str(initial, "name") : null)
                ?? throw new EntraGraphException(EntraGraphErrorKind.Unavailable,
                    "domínio inicial do locatário não permite derivar o host de administração do SharePoint", endpointPath: "/organization");

            var adminToken = await _tokens.AcquireAsync(cfg, $"https://{host}/.default", ct);
            var root = await _rest.GetAsync(adminToken, $"https://{host}/_api/SPO.Tenant", ct);
            var t = Prop(root, "d").ValueKind == JsonValueKind.Object ? Prop(root, "d") : root;
            var oneDrive = Int(t, "OneDriveSharingCapability") ?? Int(t, "ODBSharingCapability");
            add(KnightTenantConfiguration.Document(SharePointAdminTenant.ExternalId, "Configurações do SharePoint e OneDrive (administração)",
                new SharePointAdminTenant(
                    SharePointAdminTenant.SharingCapabilityName(Int(t, "SharingCapability")),
                    SharePointAdminTenant.SharingCapabilityName(oneDrive),
                    Bool(t, "EnableAzureADB2BIntegration"),
                    SharePointAdminTenant.SharingLinkTypeName(Int(t, "DefaultSharingLinkType")),
                    SharePointAdminTenant.SharingPermissionName(Int(t, "DefaultLinkPermission")),
                    Bool(t, "ExternalUserExpirationRequired"), Int(t, "ExternalUserExpireInDays"),
                    Bool(t, "EmailAttestationRequired"), Int(t, "EmailAttestationReAuthDays"),
                    Bool(t, "DisallowInfectedFileDownload"),
                    Strings(t, "GuestSharingGroupAllowListInTenantByPrincipalIdentity"),
                    Bool(t, "LegacyAuthProtocolsEnabled"))));
        }, ct);
    }
}

// ---- Microsoft Fabric ------------------------------------------------------------------------------------

/// <summary>
/// Configurações do locatário do Fabric pela API de administração (<c>GET /v1/admin/tenantsettings</c>). A Microsoft
/// documenta o acesso por entidade de serviço; o administrador do Fabric precisa permitir que entidades de serviço
/// usem as APIs de administração somente leitura, e a aplicação precisa estar no grupo de segurança autorizado.
/// </summary>
public sealed class FabricKnightCollector : KnightRestCollectorBase
{
    internal const string Scope = "https://api.fabric.microsoft.com/.default";
    internal const string TenantSettingsUrl = "https://api.fabric.microsoft.com/v1/admin/tenantsettings";

    private readonly IMicrosoftAppTokenClient _tokens;
    private readonly IMicrosoftRestClient _rest;

    public FabricKnightCollector(IMicrosoftAppTokenClient tokens, IMicrosoftRestClient rest,
        ILogger<FabricKnightCollector>? log = null, TimeProvider? time = null) : base(log, time)
    {
        _tokens = tokens;
        _rest = rest;
    }

    public override KnightSourceType Source => KnightSourceType.MicrosoftFabric;

    public override IReadOnlyList<KnightCapability> Capabilities { get; } = new[] { KnightCapability.FabricTenantSettings };

    protected override async Task CollectCoreAsync(
        IMicrosoftGraphCredentials cfg, KnightSourceConfiguration _, KnightCapabilityRecorder recorder, CancellationToken ct)
    {
        var token = await _tokens.AcquireAsync(cfg, Scope, ct);
        await recorder.RunAsync(KnightCapability.FabricTenantSettings, async add =>
        {
            await foreach (var s in _rest.GetPagedAsync(token, TenantSettingsUrl, ct))
            {
                var name = Str(s, "settingName");
                if (name is null) continue;
                static IReadOnlyList<string> Groups(JsonElement e, string prop) =>
                    Items(e, prop).Select(g => Str(g, "name") ?? Str(g, "graphId")).Where(x => x is not null).Select(x => x!).ToList();
                add(KnightTenantConfiguration.Document(name, Str(s, "title"),
                    new FabricTenantSetting(name, Str(s, "title"), Bool(s, "enabled"), Bool(s, "canSpecifySecurityGroups"),
                        Groups(s, "enabledSecurityGroups"), Groups(s, "excludedSecurityGroups"), Str(s, "tenantSettingGroup"))));
            }
        }, ct);
    }
}
