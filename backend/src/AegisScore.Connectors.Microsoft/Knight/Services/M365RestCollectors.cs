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

        var recorder = new KnightCapabilityRecorder(Label, Log, Time);
        var started = Time.GetTimestamp();
        try
        {
            await CollectCoreAsync(credentials, context.Configuration, recorder, ct);
        }
        catch (Exception ex) when (KnightFailureClassifier.IsCallerCancellation(ex, ct))
        {
            throw;
        }
        catch (Exception ex)
        {
            // [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Falha FORA de uma capacidade (token, descoberta): as capacidades ainda sem
            // desfecho recebem o mesmo motivo classificado — tempo esgotado, circuito aberto e transporte inclusive. As já
            // registradas (e os documentos delas) são preservadas: uma falha posterior não apaga leituras concluídas.
            var stage = recorder.Capabilities.Count == 0 ? "token" : "collector";
            var failure = KnightFailureClassifier.Classify(ex, Label, stage, recorder.ElapsedMs(started), recorder.CorrelationId);
            Log?.LogWarning(
                "Falha fora das capacidades em {Source}: Outcome={Outcome}, Tipo={Kind}, Estagio={Stage}, HttpStatus={Status}, Correlacao={CorrelationId}.",
                Source, failure.Outcome, failure.Diagnostic.Kind, stage, failure.Diagnostic.HttpStatus, recorder.CorrelationId);
            var state = KnightCapabilityRecorder.StateFor(failure.Outcome);
            var remaining = Capabilities.Where(c => recorder.Capabilities.All(r => r.Capability != c)).ToList();
            var result = KnightCapabilityRecorder.Failure(Source, Label, remaining, state, failure.Outcome,
                failure.Reason, Time.GetUtcNow(), failure.Diagnostic);
            if (recorder.Capabilities.Count == 0) return result;
            recorder.Capabilities.AddRange(result.Capabilities);
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
    /// <summary>
    /// [AEGIS-KNIGHT-COLLECTION-GAPS-01] GET documentado do singleton (sem <c>$select</c>): o objeto devolve a propriedade
    /// complexa <c>settings</c> (deviceManagementSettings). A forma anterior (<c>?$select=settings</c>) chegou em produção sem
    /// <c>secureByDefault</c>, e a capacidade terminava como coletada.
    /// </summary>
    internal const string SettingsUrl = "deviceManagement";
    internal const string SettingsEndpoint = "/v1.0/deviceManagement";
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
            var (settings, secureByDefault) = ReadSecureByDefault(root);
            add(KnightTenantConfiguration.Document(IntuneComplianceSettings.ExternalId, "Configurações de conformidade do Intune",
                new IntuneComplianceSettings(secureByDefault, Int(settings, "deviceComplianceCheckinThresholdDays"))));
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

    /// <summary>
    /// [AEGIS-KNIGHT-COLLECTION-GAPS-01] <c>settings.secureByDefault</c> como o contrato documenta (Boolean). Objeto ausente
    /// (ou nulo), objeto de outro tipo, propriedade ausente (ou nula) e propriedade de outro tipo são falhas de CONTRATO
    /// distintas — a capacidade não fica coletada e o motivo diz qual foi. Nenhuma vira false, conformidade ou sucesso.
    /// </summary>
    internal static (JsonElement Settings, bool SecureByDefault) ReadSecureByDefault(JsonElement root)
    {
        static EntraGraphException Contract(string code, string detail) =>
            new(EntraGraphErrorKind.Contract, detail, 200, code, SettingsEndpoint) { ContractDetail = detail };

        if (root.ValueKind != JsonValueKind.Object)
            throw Contract("DeviceManagementTypeMismatch", "a resposta não é o objeto deviceManagement");
        if (!root.TryGetProperty("settings", out var settings) || settings.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw Contract("SettingsMissing", "objeto settings ausente na resposta do deviceManagement");
        if (settings.ValueKind != JsonValueKind.Object)
            throw Contract("SettingsTypeMismatch", "settings com tipo incompatível (esperado objeto deviceManagementSettings)");
        if (!settings.TryGetProperty("secureByDefault", out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw Contract("SecureByDefaultMissing", "propriedade secureByDefault ausente em settings");
        return value.ValueKind switch
        {
            JsonValueKind.True => (settings, true),
            JsonValueKind.False => (settings, false),
            _ => throw Contract("SecureByDefaultTypeMismatch", "secureByDefault com tipo incompatível (esperado booleano)"),
        };
    }
}

// ---- SharePoint e OneDrive -------------------------------------------------------------------------------

/// <summary>
/// SharePoint e OneDrive pelo Microsoft Graph v1.0 <c>GET /admin/sharepoint/settings</c>
/// (<c>SharePointTenantSettings.Read.All</c>, somente leitura, aceita segredo): compartilhamento externo, recompartilhamento
/// por convidados, domínios, autenticação legada e sincronização em dispositivos não gerenciados.
/// <para>[AEGIS-KNIGHT-READONLY-01] A API de administração do SharePoint (objeto <c>Tenant</c>) — a única leitura oficial
/// de OneDrive, links padrão, expiração de convidados, reautenticação, download de arquivo infectado, B2B e grupos
/// autorizados — NÃO é lida: sem usuário, a Microsoft só a abre com <c>Sites.FullControl.All</c>, que também concede
/// escrita em todos os sites. Ter certificado não muda isso. A capacidade é registrada como não tentada, com o motivo, e
/// as regras usam o que o Graph sustenta (ver <c>M365ServiceControls</c>).</para>
/// </summary>
public sealed class SharePointKnightCollector : KnightRestCollectorBase
{
    internal const string GraphSettingsUrl = "admin/sharepoint/settings";

    internal const string AdminApiNotRequested =
        "Não solicitada: a API de administração do SharePoint só aceita aplicativo com Sites.FullControl.All, que também concede "
        + "escrita em todos os sites. O AEGIS opera só com leitura; configurações de OneDrive, links padrão, expiração de convidados, "
        + "reautenticação, arquivos infectados, integração B2B e grupos autorizados que o Microsoft Graph não expõe ficam não avaliadas.";

    private readonly IEntraGraphClient _graph;

    public SharePointKnightCollector(IEntraGraphClient graph, ILogger<SharePointKnightCollector>? log = null, TimeProvider? time = null)
        : base(log, time)
    {
        _graph = graph;
    }

    public override KnightSourceType Source => KnightSourceType.MicrosoftSharePoint;

    public override IReadOnlyList<KnightCapability> Capabilities { get; } = new[]
    {
        KnightCapability.SharePointTenantSettings,
        KnightCapability.SharePointAdminTenant,
    };

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

        // Com ou sem certificado: nenhum token para o host de administração e nenhuma chamada a ele.
        recorder.Record(KnightCapability.SharePointAdminTenant, KnightCapabilityOutcome.NotAttempted, AdminApiNotRequested);
    }
}

// ---- Microsoft Fabric ------------------------------------------------------------------------------------

/// <summary>
/// Configurações do locatário do Fabric pela API de administração (<c>GET /v1/admin/tenantsettings</c>). A Microsoft
/// documenta o acesso por entidade de serviço; o administrador do Fabric precisa permitir que entidades de serviço
/// usem as APIs de administração somente leitura, e a aplicação precisa estar no grupo de segurança autorizado.
/// <para>[AEGIS-KNIGHT-TEAMS-FABRIC-ACCESS-01] Contrato conferido com a referência: <c>value</c> (TenantSetting[]) e a
/// continuação por <c>continuationUri</c>/<c>continuationToken</c>. Numa recusa, o motivo preserva status, código e endpoint
/// e recebe o diagnóstico de <see cref="KnightAccessDiagnostics.FabricAsync"/> — o que o token e a entidade de serviço
/// comprovam. Os escopos delegados (Tenant.Read.All) não são recomendados: esta é autenticação de aplicativo.</para>
/// </summary>
public sealed class FabricKnightCollector : KnightRestCollectorBase
{
    internal const string Scope = "https://api.fabric.microsoft.com/.default";
    internal const string TenantSettingsUrl = "https://api.fabric.microsoft.com/v1/admin/tenantsettings";

    private readonly IMicrosoftAppTokenClient _tokens;
    private readonly IMicrosoftRestClient _rest;
    private readonly IEntraGraphClient? _graph;

    public FabricKnightCollector(IMicrosoftAppTokenClient tokens, IMicrosoftRestClient rest,
        ILogger<FabricKnightCollector>? log = null, TimeProvider? time = null, IEntraGraphClient? graph = null) : base(log, time)
    {
        _tokens = tokens;
        _rest = rest;
        _graph = graph;
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

        // [AEGIS-KNIGHT-TEAMS-FABRIC-ACCESS-01] Recusa: o motivo persistido ganha o que foi verificado, o que não é verificável
        // e a ação, quando demonstrada. O código real (ex.: InsufficientScopes) e a etapa continuam no início do motivo.
        var index = recorder.Capabilities.FindIndex(c => c.Capability == KnightCapability.FabricTenantSettings);
        if (index >= 0 && recorder.Capabilities[index].Outcome is KnightCapabilityOutcome.InsufficientPermission or KnightCapabilityOutcome.AuthenticationFailure)
        {
            var diagnosis = await DiagnoseAsync(cfg, token, ct);
            recorder.Capabilities[index] = recorder.Capabilities[index] with
            {
                Detail = recorder.Capabilities[index].Detail + " " + diagnosis.Text,
            };
        }
    }

    private async Task<KnightAccessDiagnosis> DiagnoseAsync(IMicrosoftGraphCredentials cfg, string token, CancellationToken ct)
    {
        try
        {
            return await KnightAccessDiagnostics.FabricAsync(cfg, token, _graph, ct);
        }
        catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, ct))
        {
            Log?.LogWarning("Diagnóstico de acesso do Microsoft Fabric indisponível: {ExceptionType}.", KnightFailureClassifier.SafeTypeName(ex));
            return KnightAccessDiagnosis.Unavailable;
        }
    }
}
