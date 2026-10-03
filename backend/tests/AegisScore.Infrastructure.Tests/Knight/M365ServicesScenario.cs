using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Connectors.Microsoft.Knight.Exchange;
using AegisScore.Connectors.Microsoft.Knight.Protection;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-04] Ambiente SINTÉTICO dos serviços do Microsoft 365 além do Entra, Teams e Exchange
/// ("Cliente Demo", domínios example.com): respostas do Microsoft Graph, da API administrativa do SharePoint, da API de
/// administração do Fabric, do adaptador PowerShell de proteção (Defender para Office 365 e Purview) e do DNS. Cada
/// variante liga um desfecho que o produto precisa distinguir.
/// </summary>
public sealed class M365ServicesScenario
{
    public enum Variant
    {
        /// <summary>Tudo conforme: cada controle aprova.</summary>
        Compliant,
        /// <summary>Tudo inadequado: cada controle reprova com o objeto que sustenta a reprovação.</summary>
        NonCompliant,
        /// <summary>DNS sem resposta: SPF e DMARC ficam sem veredito — nunca reprovados.</summary>
        DnsUnresolved,
        /// <summary>A sessão do Security &amp; Compliance recusada por autorização: DLP e rótulos não avaliados.</summary>
        ComplianceDenied,
        /// <summary>A API do Fabric recusa a entidade de serviço (403).</summary>
        FabricForbidden,
        /// <summary>Política personalizada mal configurada cuja regra está DESLIGADA: não alcança ninguém.</summary>
        InertPolicy,
        /// <summary>Mais domínios aceitos que o teto de consulta DNS da coleta.</summary>
        ManyDomains,
        /// <summary>[Azure] A aplicação conecta ao Resource Manager, mas não enxerga nenhuma assinatura.</summary>
        NoAzureSubscriptions,
    }

    public const string InitialDomain = "clientedemo.onmicrosoft.com";
    public const string OwnDomain = "clientedemo.example.com";

    private readonly Variant _v;

    public M365ServicesScenario(Variant v) => _v = v;

    private bool Ok => _v != Variant.NonCompliant;

    /// <summary>Pedidos recebidos pelas APIs HTTP (token, Graph, SharePoint, Fabric), na ordem.</summary>
    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>Corpos dos pedidos de token (para conferir segredo × certificado).</summary>
    public List<string> TokenBodies { get; } = new();

    // ---- Certificado sintético (gerado no próprio teste; nunca um certificado real) --------------------------

    private static readonly Lazy<(X509Certificate2 Cert, string PfxBase64)> Cert = new(() =>
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=AEGIS Cliente Demo (sintético)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var pfx = cert.Export(X509ContentType.Pkcs12, "senha-sintetica");
        return (X509CertificateLoader.LoadPkcs12(pfx, "senha-sintetica"), Convert.ToBase64String(pfx));
    });

    public static X509Certificate2 Certificate => Cert.Value.Cert;

    public static MicrosoftClientCertificate ClientCertificate => new(Cert.Value.PfxBase64, "senha-sintetica");

    public static KnightMicrosoftServiceConfiguration Configuration(KnightSourceType source, bool withCertificate = true) =>
        new(source, "dir-demo-0001", "client-demo", "segredo-sintetico", withCertificate ? ClientCertificate : null);

    // ---- HTTP ---------------------------------------------------------------------------------------------

    public HttpMessageHandler Handler => new StubHandler(this);

    private (HttpStatusCode, string) Respond(HttpRequestMessage request, string? body)
    {
        var url = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);
        if (url.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
        {
            lock (TokenBodies) TokenBodies.Add(body ?? "");
            return (HttpStatusCode.OK, """{"token_type":"Bearer","expires_in":3600,"access_token":"token-sintetico"}""");
        }

        if (url.Contains("/deviceManagement/deviceEnrollmentConfigurations", StringComparison.OrdinalIgnoreCase)) return (HttpStatusCode.OK, Enrollment());
        if (url.Contains("/deviceManagement?", StringComparison.OrdinalIgnoreCase)) return (HttpStatusCode.OK, IntuneSettings());
        if (url.Contains("/admin/sharepoint/settings", StringComparison.OrdinalIgnoreCase)) return (HttpStatusCode.OK, SharePointGraph());
        if (url.Contains("/organization", StringComparison.OrdinalIgnoreCase))
            return (HttpStatusCode.OK, $$"""{"value":[{"verifiedDomains":[{"name":"{{InitialDomain}}","isInitial":true},{"name":"{{OwnDomain}}","isInitial":false}]}]}""");
        if (url.StartsWith("https://clientedemo-admin.sharepoint.com/_api/SPO.Tenant", StringComparison.OrdinalIgnoreCase)) return (HttpStatusCode.OK, SharePointAdmin());
        if (url.StartsWith("https://management.azure.com/subscriptions?", StringComparison.OrdinalIgnoreCase))
            return (HttpStatusCode.OK, _v == Variant.NoAzureSubscriptions
                ? """{"value":[]}"""
                : """{"value":[{"subscriptionId":"00000000-aaaa-4000-8000-00000000000a","displayName":"Assinatura Demo","state":"Enabled"}]}""");
        if (url.StartsWith("https://api.fabric.microsoft.com/v1/admin/tenantsettings", StringComparison.OrdinalIgnoreCase))
        {
            if (_v == Variant.FabricForbidden)
                return (HttpStatusCode.Forbidden, """{"errorCode":"Unauthorized","message":"sintético"}""");
            return url.Contains("continuationToken=pagina-2", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, FabricPage2())
                : (HttpStatusCode.OK, FabricPage1());
        }
        return (HttpStatusCode.NotFound, """{"error":{"code":"NotFound","message":"rota sintética inexistente"}}""");
    }

    private string IntuneSettings() =>
        $$"""{"settings":{"secureByDefault":{{(Ok ? "true" : "false")}},"deviceComplianceCheckinThresholdDays":30} }""";

    private string Enrollment()
    {
        string P(bool personalBlocked, bool platformBlocked = false) =>
            $$"""{"platformBlocked":{{platformBlocked.ToString().ToLowerInvariant()}},"personalDeviceEnrollmentBlocked":{{personalBlocked.ToString().ToLowerInvariant()}} }""";
        return $$"""
        {"value":[
          {"@odata.type":"#microsoft.graph.deviceEnrollmentPlatformRestrictionsConfiguration","id":"demo_DefaultPlatformRestrictions",
           "displayName":"Todos os usuários e dispositivos","priority":0,
           "windowsRestriction":{{P(Ok)}},"iosRestriction":{{P(true)}},"androidRestriction":{{P(false, platformBlocked: true)}},
           "androidForWorkRestriction":{{P(true)}},"macOSRestriction":{{P(Ok)}},"assignments":[]},
          {"@odata.type":"#microsoft.graph.deviceEnrollmentPlatformRestrictionsConfiguration","id":"demo_grupo_ti",
           "displayName":"TI (sintético)","priority":1,"windowsRestriction":{{P(false)}},"assignments":[{"id":"a1"}]},
          {"@odata.type":"#microsoft.graph.deviceEnrollmentLimitConfiguration","id":"demo_limite","displayName":"Limite","priority":0,"limit":15}
        ]}
        """;
    }

    private string SharePointGraph() => Ok
        ? """
          {"sharingCapability":"externalUserSharingOnly","sharingDomainRestrictionMode":"allowList",
           "sharingAllowedDomainList":["parceiro.example.com"],"sharingBlockedDomainList":[],
           "isResharingByExternalUsersEnabled":false,"isLegacyAuthProtocolsEnabled":false,
           "isUnmanagedSyncAppForTenantRestricted":true,"allowedDomainGuidsForSyncApp":["00000000-0000-0000-0000-00000000d0d0"],
           "isRequireAcceptingUserToMatchInvitedUserEnabled":true}
          """
        : """
          {"sharingCapability":"externalUserAndGuestSharing","sharingDomainRestrictionMode":"none",
           "sharingAllowedDomainList":[],"sharingBlockedDomainList":[],
           "isResharingByExternalUsersEnabled":true,"isLegacyAuthProtocolsEnabled":true,
           "isUnmanagedSyncAppForTenantRestricted":false,"allowedDomainGuidsForSyncApp":[],
           "isRequireAcceptingUserToMatchInvitedUserEnabled":false}
          """;

    private string SharePointAdmin() => Ok
        ? """
          {"SharingCapability":1,"OneDriveSharingCapability":0,"EnableAzureADB2BIntegration":true,"DefaultSharingLinkType":1,
           "DefaultLinkPermission":1,"ExternalUserExpirationRequired":true,"ExternalUserExpireInDays":30,
           "EmailAttestationRequired":true,"EmailAttestationReAuthDays":15,"DisallowInfectedFileDownload":true,
           "GuestSharingGroupAllowListInTenantByPrincipalIdentity":["c:0t.c|tenant|00000000-0000-0000-0000-0000000000aa"],
           "LegacyAuthProtocolsEnabled":false}
          """
        : """
          {"SharingCapability":2,"OneDriveSharingCapability":2,"EnableAzureADB2BIntegration":false,"DefaultSharingLinkType":3,
           "DefaultLinkPermission":2,"ExternalUserExpirationRequired":false,"ExternalUserExpireInDays":60,
           "EmailAttestationRequired":false,"EmailAttestationReAuthDays":30,"DisallowInfectedFileDownload":false,
           "GuestSharingGroupAllowListInTenantByPrincipalIdentity":[],"LegacyAuthProtocolsEnabled":true}
          """;

    /// <summary>
    /// Configurações do Fabric em DUAS páginas (a segunda por <c>continuationUri</c>), como a API documenta. No cenário
    /// conforme, metade está desligada e metade restrita a grupo — as duas formas aprovam.
    /// </summary>
    private string FabricPage1() =>
        $$"""{"value":[{{string.Join(",", FabricSettings().Take(6))}}],"continuationUri":"https://api.fabric.microsoft.com/v1/admin/tenantsettings?continuationToken=pagina-2"}""";

    private string FabricPage2() => "{\"value\":[" + string.Join(",", FabricSettings().Skip(6)) + "]}";

    private IEnumerable<string> FabricSettings()
    {
        string S(string name, bool enabled, bool group = false) =>
            "{\"settingName\":\"" + name + "\",\"title\":\"" + name + " (sintético)\",\"enabled\":" + enabled.ToString().ToLowerInvariant()
            + ",\"canSpecifySecurityGroups\":true,\"tenantSettingGroup\":\"Demo\""
            + (group ? ",\"enabledSecurityGroups\":[{\"graphId\":\"00000000-0000-0000-0000-0000000000fb\",\"name\":\"Fabric Admins (sintético)\"}]" : "")
            + "}";
        // Esperado "desligada ou só para grupos": conforme alterna desligada/grupo; inadequado liga para todos.
        var restrictable = new[] { "AllowGuestUserToAccessSharedContent", "ExternalSharingV2", "ElevatedGuestsTenant", "PublishToWeb",
            "ShareLinkToEntireOrg", "AllowExternalDataSharingSwitch", "ServicePrincipalAccessGlobalAPIs",
            "AllowServicePrincipalsCreateAndUseProfiles", "ServicePrincipalAccessPermissionAPIs" };
        for (var i = 0; i < restrictable.Length; i++)
            yield return Ok ? S(restrictable[i], enabled: i % 2 == 1, group: i % 2 == 1) : S(restrictable[i], enabled: true);
        yield return S("RScriptVisual", enabled: !Ok);                 // esperado desligada
        yield return S("EimInformationProtectionEdit", enabled: Ok);    // esperado ligada
        yield return S("BlockResourceKeyAuthentication", enabled: Ok);  // esperado ligada
        yield return S("AdminApisIncludeDetailedMetadata", enabled: true); // sem controle: ignorada pelas regras
    }

    // ---- Adaptador PowerShell de proteção ------------------------------------------------------------------

    public IProtectionAdminReader ProtectionReader => new StubProtectionReader(this);

    public List<ProtectionAdminRequest> ProtectionRequests { get; } = new();

    private ProtectionAdminOutput Protection(ProtectionAdminRequest request)
    {
        lock (ProtectionRequests) ProtectionRequests.Add(request);
        var reads = request.Profile == ProtectionAdminRequest.Defender ? DefenderReads() : PurviewReads();
        var complianceOk = _v != Variant.ComplianceDenied;
        if (request.Profile == ProtectionAdminRequest.Purview && !complianceOk)
            reads = reads.Where(r => r.Capability == nameof(KnightCapability.PurviewAuditConfig)).ToList();
        return new ProtectionAdminOutput(
            new ExchangeAdminOutput(new ExchangeAdminRuntime("7.4.6", "3.9.2", "Linux"), true, null, null, 5000, reads),
            complianceOk, complianceOk ? null : nameof(KnightCapabilityOutcome.InsufficientPermission));
    }

    private static ExchangeAdminRead Read(KnightCapability capability, string command, string itemsJson, bool truncated = false) =>
        new(capability.ToString(), command, true, JsonDocument.Parse(itemsJson).RootElement.Clone(), truncated, null, null);

    private static string Reach(bool isDefault, string state = "Enabled", string sentTo = "[]") => isDefault
        ? """ "isDefault":true,"reach":{"hasRule":false} """
        : $$""" "isDefault":false,"reach":{"hasRule":true,"state":"{{state}}","priority":0,"sentTo":{{sentTo}},"sentToMemberOf":[],"recipientDomainIs":["{{OwnDomain}}"]} """;

    private IReadOnlyList<ExchangeAdminRead> DefenderReads()
    {
        var b = Ok ? "true" : "false";
        var nb = Ok ? "false" : "true";
        var allTypes = string.Join(",", Application.Knight.Catalog.DefenderForOffice365Controls.HighRiskExtensions.Select(e => $"\"{e}\""));
        var fileTypes = Ok ? allTypes : "\"exe\",\"bat\"";
        // Variante de política INERTE: uma personalizada inadequada com regra desligada, ao lado da padrão conforme.
        var inert = _v == Variant.InertPolicy
            ? $$""",{"identity":"Personalizada desligada","name":"Personalizada desligada","enableFileFilter":false,"fileTypes":[],{{Reach(false, state: "Disabled")}} }"""
            : "";

        var domains = new List<string> { InitialDomain, OwnDomain };
        if (_v == Variant.ManyDomains)
            domains.AddRange(Enumerable.Range(1, DefenderForOffice365KnightCollector.MaxDnsDomains).Select(i => $"filial{i:000}.example.com"));

        return new[]
        {
            Read(KnightCapability.DefenderAtpPolicy, "Get-AtpPolicyForO365",
                $$"""[{"enableATPForSPOTeamsODB":{{b}},"enableSafeDocs":{{b}},"allowSafeDocsOpen":{{nb}} }]"""),
            Read(KnightCapability.DefenderSafeLinks, "Get-SafeLinksPolicy",
                $$"""[{"identity":"Built-In Protection Policy","name":"Built-In Protection Policy","isBuiltInProtection":true,"enableSafeLinksForEmail":true,"enableSafeLinksForTeams":true,"enableSafeLinksForOffice":true,"trackClicks":true,"allowClickThrough":false,"scanUrls":true,"isDefault":false,"reach":{"hasRule":false} }, {"identity":"Links Demo","name":"Links Demo","enableSafeLinksForEmail":true,"enableSafeLinksForTeams":{{b}},"enableSafeLinksForOffice":true,"trackClicks":true,"allowClickThrough":{{nb}},"scanUrls":true,{{Reach(false)}} }]"""),
            Read(KnightCapability.DefenderSafeAttachments, "Get-SafeAttachmentPolicy",
                $$"""[{"identity":"Anexos Demo","name":"Anexos Demo","enable":true,"action":"{{(Ok ? "Block" : "Allow")}}",{{Reach(false)}} }]"""),
            Read(KnightCapability.DefenderMalwareFilter, "Get-MalwareFilterPolicy",
                $$"""[{"identity":"Default","name":"Default","enableFileFilter":{{b}},"fileTypes":[{{fileTypes}}],"enableInternalSenderAdminNotifications":{{b}},"internalSenderAdminAddressSet":{{b}},"zapEnabled":true,{{Reach(true)}} }{{inert}}]"""),
            Read(KnightCapability.DefenderInboundSpam, "Get-HostedContentFilterPolicy",
                $$"""[{"identity":"Default","name":"Default","allowedSenderDomains":[{{(Ok ? "" : "\"fornecedor.example.com\"")}}],"allowedSendersCount":0,{{Reach(true)}} }]"""),
            Read(KnightCapability.DefenderOutboundSpam, "Get-HostedOutboundSpamFilterPolicy",
                $$"""[{"identity":"Default","name":"Default","recipientLimitExternalPerHour":{{(Ok ? 400 : 0)}},"recipientLimitInternalPerHour":{{(Ok ? 800 : 0)}},"recipientLimitPerDay":{{(Ok ? 800 : 0)}},"actionWhenThresholdReached":"{{(Ok ? "BlockUser" : "Alert")}}","notifyOutboundSpam":{{b}},"notifyOutboundSpamRecipientsCount":{{(Ok ? 1 : 0)}},"bccSuspiciousOutboundMail":{{b}},"bccSuspiciousOutboundAdditionalRecipientsCount":{{(Ok ? 1 : 0)}},{{Reach(true)}} }]"""),
            Read(KnightCapability.DefenderConnectionFilter, "Get-HostedConnectionFilterPolicy",
                $$"""[{"identity":"Default","name":"Default","ipAllowList":[{{(Ok ? "" : "\"192.0.2.10\"")}}],"enableSafeList":{{nb}} }]"""),
            Read(KnightCapability.DefenderAntiPhish, "Get-AntiPhishPolicy",
                $$"""[{"identity":"Office365 AntiPhish Default","name":"Office365 AntiPhish Default","enabled":true,"phishThresholdLevel":{{(Ok ? 3 : 1)}},"enableTargetedUserProtection":{{b}},"targetedUsersToProtectCount":{{(Ok ? 2 : 0)}},"enableOrganizationDomainsProtection":{{b}},"enableMailboxIntelligence":true,"enableMailboxIntelligenceProtection":{{b}},"enableSpoofIntelligence":true,"targetedUserProtectionAction":"{{(Ok ? "Quarantine" : "NoAction")}}","targetedDomainProtectionAction":"{{(Ok ? "Quarantine" : "NoAction")}}","mailboxIntelligenceProtectionAction":"{{(Ok ? "Quarantine" : "NoAction")}}",{{Reach(true)}} }]"""),
            Read(KnightCapability.DefenderDkim, "Get-DkimSigningConfig",
                $$"""[{"domain":"{{OwnDomain}}","enabled":{{b}},"status":"{{(Ok ? "Valid" : "CnameMissing")}}"}]"""),
            Read(KnightCapability.DefenderAcceptedDomains, "Get-AcceptedDomain",
                "[" + string.Join(",", domains.Select((d, i) => $$"""{"domainName":"{{d}}","domainType":"Authoritative","isDefault":{{(i == 1).ToString().ToLowerInvariant()}} }""")) + "]"),
            Read(KnightCapability.DefenderTeamsProtection, "Get-TeamsProtectionPolicy",
                $$"""[{"identity":"Teams Protection Policy","zapEnabled":{{b}} }]"""),
            Read(KnightCapability.DefenderPriorityAccounts, "Get-EmailTenantSettings; Get-User -IsVIP",
                $$"""[{"enablePriorityAccountProtection":{{b}},"listComplete":true,"accounts":[{"externalDirectoryObjectId":"00000000-0000-0000-0000-0000000000c1","userPrincipalName":"diretoria@{{OwnDomain}}","displayName":"Diretoria (sintético)"}]}]"""),
            Read(KnightCapability.DefenderPresetPolicies, "Get-EOPProtectionPolicyRule; Get-ATPProtectionPolicyRule",
                $$"""[{"kind":"EOP","identity":"Strict Preset Security Policy","name":"Strict Preset Security Policy","state":"Enabled","priority":0,"sentTo":[{{(Ok ? $"\"diretoria@{OwnDomain}\"" : "")}}],"sentToMemberOf":[],"recipientDomainIs":[]}, {"kind":"ATP","identity":"Strict Preset Security Policy","name":"Strict Preset Security Policy","state":"Enabled","priority":0,"sentTo":[{{(Ok ? $"\"diretoria@{OwnDomain}\"" : "")}}],"sentToMemberOf":[],"recipientDomainIs":[]}]"""),
            // [AEGIS-KNIGHT-CLOSURE-01] Destino das mensagens denunciadas: o adaptador só devolve quantos endereços existem.
            Read(KnightCapability.DefenderReportSubmissionPolicy, "Get-ReportSubmissionPolicy",
                $$"""[{"identity":"DefaultReportSubmissionPolicy","reportJunkToCustomizedAddress":{{b}},"reportNotJunkToCustomizedAddress":{{b}},"reportPhishToCustomizedAddress":{{b}},"reportJunkAddresses":{{(Ok ? 1 : 0)}},"reportNotJunkAddresses":{{(Ok ? 1 : 0)}},"reportPhishAddresses":{{(Ok ? 1 : 0)}},"reportChatMessageEnabled":{{(Ok ? "false" : "true")}},"reportChatMessageToCustomizedAddressEnabled":{{b}},"reportChatMessageAddresses":{{(Ok ? 1 : 0)}} }]"""),
        };
    }

    private IReadOnlyList<ExchangeAdminRead> PurviewReads()
    {
        var b = Ok ? "true" : "false";
        return new[]
        {
            Read(KnightCapability.PurviewAuditConfig, "Get-AdminAuditLogConfig", $$"""[{"unifiedAuditLogIngestionEnabled":{{b}} }]"""),
            Read(KnightCapability.PurviewDlpPolicies, "Get-DlpCompliancePolicy", Ok
                ? """
                  [{"identity":"DLP Dados Pessoais","name":"DLP Dados Pessoais","mode":"Enable","enabled":true,"workloads":["Exchange","SharePoint","OneDriveForBusiness","Teams"],"exchangeLocation":["All"],"sharePointLocation":["All"],"oneDriveLocation":["All"],"teamsLocation":["All"],"enforcementPlanes":[],"applicationLocations":[]},
                   {"identity":"DLP Copilot","name":"DLP Copilot","mode":"Enable","enabled":true,"workloads":["Applications"],"exchangeLocation":[],"sharePointLocation":[],"oneDriveLocation":[],"teamsLocation":[],"enforcementPlanes":["CopilotExperiences"],"applicationLocations":["470f2276-e011-4e9d-a6ec-20768be3a4b0"]}]
                  """
                : """
                  [{"identity":"DLP em teste","name":"DLP em teste","mode":"TestWithNotifications","enabled":true,"workloads":["Exchange","Teams"],"exchangeLocation":["All"],"sharePointLocation":[],"oneDriveLocation":[],"teamsLocation":["All"],"enforcementPlanes":[],"applicationLocations":[]}]
                  """),
            Read(KnightCapability.PurviewLabelPolicies, "Get-LabelPolicy", Ok
                ? """[{"identity":"Rótulos Demo","name":"Rótulos Demo","enabled":true,"mode":"Enforce","labels":["Público","Interno","Confidencial"],"locationCount":2}]"""
                : """[{"identity":"Rótulos desligados","name":"Rótulos desligados","enabled":false,"mode":"Enforce","labels":["Confidencial"],"locationCount":1}]"""),
        };
    }

    // ---- DNS ----------------------------------------------------------------------------------------------

    public IDnsTxtResolver Dns => new StubDns(this);

    private DnsTxtResult Txt(string name)
    {
        if (_v == Variant.DnsUnresolved) return DnsTxtResult.NotResolved("tempo esgotado");
        var dmarc = name.StartsWith("_dmarc.", StringComparison.OrdinalIgnoreCase);
        if (Ok)
            return DnsTxtResult.Found(dmarc
                ? new[] { "v=DMARC1; p=reject; rua=mailto:dmarc@clientedemo.example.com" }
                : new[] { "v=spf1 include:spf.protection.outlook.com -all", "google-site-verification=sintetico" });
        return DnsTxtResult.Found(dmarc
            ? new[] { "v=DMARC1; p=none" }
            : new[] { "v=spf1 include:_spf.example.com ~all" });
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly M365ServicesScenario _s;
        public StubHandler(M365ServicesScenario s) => _s = s;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            lock (_s.Requests) _s.Requests.Add(request);
            var (status, json) = _s.Respond(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StubProtectionReader : IProtectionAdminReader
    {
        private readonly M365ServicesScenario _s;
        public StubProtectionReader(M365ServicesScenario s) => _s = s;
        public Task<ProtectionAdminOutput> ReadAsync(ProtectionAdminRequest request, CancellationToken ct = default) => Task.FromResult(_s.Protection(request));
        public Task<ProtectionAdminOutput> TestConnectionAsync(ProtectionAdminRequest request, CancellationToken ct = default) => Task.FromResult(_s.Protection(request));
    }

    private sealed class StubDns : IDnsTxtResolver
    {
        private readonly M365ServicesScenario _s;
        public StubDns(M365ServicesScenario s) => _s = s;
        public Task<DnsTxtResult> ResolveTxtAsync(string name, CancellationToken ct) => Task.FromResult(_s.Txt(name));
    }

    /// <summary>Token do Exchange Online sintético (o domínio inicial viria do Graph na mesma aquisição).</summary>
    public sealed class StubExchangeTokens : IExchangeTokenClient
    {
        public Task<ExchangeAdminCredentials> AcquireAsync(IMicrosoftGraphCredentials credentials, CancellationToken ct = default) =>
            Task.FromResult(new ExchangeAdminCredentials(InitialDomain, "token-sintetico-exo"));
    }
}
