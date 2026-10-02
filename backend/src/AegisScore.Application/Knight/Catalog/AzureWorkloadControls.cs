using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Application.Knight.Reference;
using AegisScore.Domain;
using static AegisScore.Application.Knight.Catalog.AzureRuleKit;
using static AegisScore.Application.Knight.Catalog.KnightRuleKit;

namespace AegisScore.Application.Knight.Catalog;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-04] Controles do Azure: computação, App Service, bancos de dados e Databricks
// ============================================================================
// No App Service, a mesma checagem vale para aplicativos, funções e slots: o controle avalia a população inteira (cada
// slot é um recurso próprio, com configuração própria) e cita as referências de cada variante.

/// <summary>Leituras comuns do App Service.</summary>
internal static class AzureApp
{
    public static IEnumerable<AzureResource> Sites(AzureView v) =>
        v.OfType("Microsoft.Web/sites").Concat(v.OfType("Microsoft.Web/sites/slots"));

    public static bool IsFunction(AzureResource r) => r.Kind?.Contains("functionapp", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsSlot(AzureResource r) => r.Is("Microsoft.Web/sites/slots");

    /// <summary>Configuração do site não lida: a checagem que depende dela é desconhecida, não reprovada.</summary>
    public static KnightItemCheck? ConfigMissing(AzureResource r, string key, string expected) =>
        r.Str(key + ":status") is { } st ? KnightItemCheck.Of(null, $"configuração do aplicativo não lida ({st})", expected) : null;

    private static readonly Dictionary<string, string[]> LinuxPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["java"] = new[] { "JAVA|", "TOMCAT|", "JBOSSEAP|" },
        ["python"] = new[] { "PYTHON|" },
        ["php"] = new[] { "PHP|" },
    };

    /// <summary>Versão de runtime declarada pelo aplicativo (Linux: linuxFxVersion; Windows: a propriedade da linguagem).</summary>
    public static (string Os, string Version)? Runtime(AzureResource r, string language)
    {
        var fx = r.Str("web:properties.linuxFxVersion");
        if (!string.IsNullOrWhiteSpace(fx))
            return LinuxPrefixes[language].Any(p => fx.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ? ("linux", fx) : null;
        var win = r.Str($"web:properties.{language}Version");
        return string.IsNullOrWhiteSpace(win) ? null : ("windows", win);
    }

    public static IEnumerable<AzureResource> UsingRuntime(AzureView v, string language) => Sites(v).Where(s => Runtime(s, language) is not null);

    /// <summary>Compara a versão em uso com as pilhas publicadas pela Microsoft (obsoleta ou fim de suporte = reprovado).</summary>
    public static KnightItemCheck RuntimeSupported(AzureResource r, AzureView v, string language)
    {
        var (os, version) = Runtime(r, language)!.Value;
        var stackType = IsFunction(r) ? "Microsoft.Web/functionAppStacks" : "Microsoft.Web/webAppStacks";
        const string expected = "versão suportada (não obsoleta e dentro do suporte)";
        if (v.TenantRead(stackType) != "Collected")
            return KnightItemCheck.Of(null, $"versão {version}; pilhas de runtime publicadas pela Microsoft não lidas nesta coleta", expected);
        var rows = v.OfType(stackType).SelectMany(st => st.Items("versions:items")).Where(x => AzureJson.Str(x, "os") == os).ToList();
        var match = rows.FirstOrDefault(x =>
            string.Equals(AzureJson.Str(x, "linuxFxVersion"), version, StringComparison.OrdinalIgnoreCase)
            || string.Equals(AzureJson.Str(x, "runtimeVersion"), version, StringComparison.OrdinalIgnoreCase)
            || (os == "windows" && string.Equals(AzureJson.Str(x, "javaVersion"), version, StringComparison.OrdinalIgnoreCase)));
        if (match.ValueKind != JsonValueKind.Object)
            return KnightItemCheck.Of(null, $"versão {version} ({os}) não encontrada nas pilhas publicadas — não aprova nem reprova", expected);
        var deprecated = AzureJson.Bool(match, "isDeprecated") == true;
        var eol = Date(AzureJson.Prop(match, "endOfLifeDate"));
        var expired = eol is not null && eol < v.Now;
        return KnightItemCheck.Of(!deprecated && !expired,
            $"versão {version} ({os})" + (deprecated ? ", marcada como obsoleta" : "") + (eol is null ? "" : expired ? $", fim do suporte em {Day(eol.Value)}" : $", suporte até {Day(eol.Value)}"),
            expected);
    }

    public static KnightItemCheck PrivateEndpoint(AzureResource r)
    {
        if (r.Str("pe:status") is { } st) return KnightItemCheck.Of(null, $"endpoints privados não lidos ({st})", "ao menos uma conexão de endpoint privado aprovada");
        var approved = r.Items("pe:items").Count(e =>
            string.Equals(AzureJson.Str(e, "properties.privateLinkServiceConnectionState.status"), "Approved", StringComparison.OrdinalIgnoreCase));
        return KnightItemCheck.Of(r.Has("pe:items") ? approved > 0 : null,
            approved > 0 ? $"{approved} conexão(ões) de endpoint privado aprovada(s)" : "nenhuma conexão de endpoint privado aprovada",
            "ao menos uma conexão de endpoint privado aprovada");
    }

    /// <summary>Conexões de endpoint privado lidas na subcoleção do aplicativo ("pe:items").</summary>
    public static IReadOnlyList<JsonElement> PrivateEndpointConnections(AzureResource r) => r.Items("pe:items");

    public static bool HasApprovedPrivateEndpoint(AzureResource r) =>
        PrivateEndpointConnections(r).Any(e =>
            string.Equals(AzureJson.Str(e, "properties.privateLinkServiceConnectionState.status"), "Approved", StringComparison.OrdinalIgnoreCase));

    // Camadas do plano com endpoint privado, pela documentação da Microsoft: App Service (Basic, Standard, PremiumV2,
    // PremiumV3, PremiumV4, IsolatedV2, Functions Premium) e Azure Functions (Flex Consumption, Elastic Premium,
    // Dedicated). Premium0V3/PremiumMV3 são ofertas da PremiumV3 e IsolatedMV2 da IsolatedV2. Sem suporte documentado:
    // Free e Shared (fora da lista) e o plano de Consumo (Dynamic: "Private endpoints aren't supported on the Consumption plan").
    private static readonly HashSet<string> PrivateEndpointTiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Basic", "Standard", "PremiumV2", "PremiumV3", "Premium0V3", "PremiumMV3", "PremiumV4", "IsolatedV2", "IsolatedMV2",
        "ElasticPremium", "FlexConsumption",
    };

    private static readonly HashSet<string> NoPrivateEndpointTiers = new(StringComparer.OrdinalIgnoreCase) { "Free", "Shared", "Dynamic" };

    public static KnightItemCheck PlanSupportsPrivateEndpoint(AzureResource plan)
    {
        var tier = plan.Str("sku.tier");
        const string expected = "camada com suporte a endpoint privado (Basic ou superior; Functions Premium ou Flex Consumption)";
        var found = $"camada {tier ?? "não informada"}{(plan.Str("sku.name") is { } n ? $" ({n})" : "")}";
        if (tier is null) return KnightItemCheck.Of(null, found, expected);
        if (PrivateEndpointTiers.Contains(tier)) return KnightItemCheck.Of(true, found, expected);
        if (NoPrivateEndpointTiers.Contains(tier)) return KnightItemCheck.Of(false, found + " — sem suporte a endpoint privado", expected);
        return KnightItemCheck.Of(null, found + " — camada fora da lista documentada; não aprova nem reprova", expected);
    }

    public static string? ClusterSetting(AzureResource ase, string name) =>
        ase.Items("properties.clusterSettings")
            .Where(e => string.Equals(AzureJson.Str(e, "name"), name, StringComparison.OrdinalIgnoreCase))
            .Select(e => AzureJson.Str(e, "value")).FirstOrDefault();
}

/// <summary>Leituras comuns dos bancos de dados.</summary>
internal static class AzureDb
{
    /// <summary>Parâmetro de servidor (PostgreSQL/MySQL) lido pela configuração filha.</summary>
    public static (string? Value, string? Status) Param(AzureResource r, string name) =>
        (r.Str($"cfg.{name}:properties.value"), r.Str($"cfg.{name}:status"));

    public static KnightItemCheck ParamCheck(AzureResource r, string name, Func<string, bool> ok, string expected)
    {
        var (value, status) = Param(r, name);
        if (status is not null) return KnightItemCheck.Of(null, $"parâmetro {name} não lido ({status})", $"{name} {expected}");
        return KnightItemCheck.Of(value is null ? null : ok(value.Trim()), $"{name} = {value ?? "não informado"}", $"{name} {expected}");
    }

    public static bool On(string v) => v.Equals("ON", StringComparison.OrdinalIgnoreCase) || v.Equals("1", StringComparison.Ordinal)
                                       || v.Equals("TRUE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lista de versões de TLS (ex.: "TLSv1.2,TLSv1.3"): todas 1.2 ou superiores.</summary>
    public static bool Tls12Plus(string v)
    {
        var parts = v.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts.All(p => p.EndsWith("1.2", StringComparison.OrdinalIgnoreCase) || p.EndsWith("1.3", StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<AzureResource> SqlServers(AzureView v) => v.OfType("Microsoft.Sql/servers");
    public static IEnumerable<AzureResource> ManagedInstances(AzureView v) => v.OfType("Microsoft.Sql/managedInstances");
    public static IEnumerable<AzureResource> Postgres(AzureView v) => v.OfType("Microsoft.DBforPostgreSQL/flexibleServers");
    public static IEnumerable<AzureResource> MySql(AzureView v) => v.OfType("Microsoft.DBforMySQL/flexibleServers");
    public static IEnumerable<AzureResource> Cosmos(AzureView v) => v.OfType("Microsoft.DocumentDB/databaseAccounts");
    public static IEnumerable<AzureResource> Redis(AzureView v) => v.OfType("Microsoft.Cache/Redis");
    public static IEnumerable<AzureResource> RedisEnterprise(AzureView v) => v.OfType("Microsoft.Cache/redisEnterprise");

    public static KnightItemCheck FirewallRules(AzureResource server, Func<string, string, bool> offending, string label)
    {
        if (server.Str("fw:status") is { } st) return KnightItemCheck.Of(null, $"regras de firewall não lidas ({st})", $"nenhuma regra que libere {label}");
        var rules = server.Items("fw:items")
            .Where(r => offending(AzureJson.Str(r, "properties.startIpAddress") ?? "", AzureJson.Str(r, "properties.endIpAddress") ?? ""))
            .Select(r => AzureJson.Str(r, "name") ?? "?").ToList();
        return KnightItemCheck.Of(server.Has("fw:items") ? rules.Count == 0 : null,
            rules.Count == 0 ? $"nenhuma regra libera {label}" : $"regra(s) {List(rules, 3)} liberam {label}", $"nenhuma regra que libere {label}");
    }

    /// <summary>Faixa de IPv4 que cobre (quase) toda a internet: início 0.0.0.0 e fim 255.255.255.255.</summary>
    public static bool WholeInternet(string start, string end) => start == "0.0.0.0" && end == "255.255.255.255";

    public static bool AllAzure(string start, string end) => start == "0.0.0.0" && end == "0.0.0.0";

    public static KnightItemCheck Tde(AzureResource db) =>
        db.Str("tde:status") is { } st
            ? KnightItemCheck.Of(null, $"estado do TDE não lido ({st})", "TDE habilitado")
            : OneOf(db, "tde:properties.state", "criptografia transparente de dados", "Enabled", "Enabled");

    public static KnightItemCheck CmkProtector(AzureResource server) =>
        server.Str("tdeProtector:status") is { } st
            ? KnightItemCheck.Of(null, $"protetor do TDE não lido ({st})", "protetor do TDE no Azure Key Vault (chave do cliente)")
            : OneOf(server, "tdeProtector:properties.serverKeyType", "tipo do protetor do TDE", "AzureKeyVault (chave do cliente)", "AzureKeyVault");
}

public static class AzureWorkloadControls
{
    private const KnightIndicatorCategory Cloud = KnightIndicatorCategory.CloudInfrastructure;

    private static AzureCheck App(string noun, Func<AzureView, IEnumerable<AzureResource>> pop, Func<AzureResource, AzureView, KnightItemCheck> check,
        string exposed, string passed) => new(KnightCapability.AzureAppService, noun, pop, check, exposed, passed);

    private static AzureCheck Compute(string noun, Func<AzureView, IEnumerable<AzureResource>> pop, Func<AzureResource, AzureView, KnightItemCheck> check,
        string exposed, string passed) => new(KnightCapability.AzureCompute, noun, pop, check, exposed, passed);

    private static AzureCheck Db(string noun, Func<AzureView, IEnumerable<AzureResource>> pop, Func<AzureResource, AzureView, KnightItemCheck> check,
        string exposed, string passed) => new(KnightCapability.AzureDatabases, noun, pop, check, exposed, passed);

    private static AzureCheck Dbr(Func<AzureResource, AzureView, KnightItemCheck> check, string exposed, string passed,
        params KnightCapability[] also) =>
        new(KnightCapability.AzureDatabricks, "workspace do Databricks", v => v.OfType("Microsoft.Databricks/workspaces"), check, exposed, passed, also);

    /// <summary>As quatro variantes do App Service (aplicativo, slot, função, slot de função) de uma checagem.</summary>
    private static KnightReferenceLink[] AppRefs(string app, string appSlot, string func, string funcSlot) => new[]
    {
        Ref(AzureRefs.Azc + app + "#aplicativo"), Ref(AzureRefs.Azc + appSlot + "#slot-de-aplicativo"),
        Ref(AzureRefs.Azc + func + "#funcao"), Ref(AzureRefs.Azc + funcSlot + "#slot-de-funcao"),
    };

    private static KnightIndicatorDefinition RuntimeControl(string id, string language, string label, KnightReferenceLink[] refs) =>
        AzureRuleKit.Control(KnightService.AzureAppService, id, $"Aplicativos com versão do {label} obsoleta ou sem suporte", Cloud, SeverityLevel.Medium,
            $"Atualizar os aplicativos, funções e slots que usam {label} para uma versão ainda suportada pelo App Service.",
            $"Versão do {label} em uso (linuxFxVersion ou {language}Version) presente nas pilhas publicadas pela Microsoft, sem marca de obsoleta e dentro da data de suporte.",
            App($"aplicativo com {label}", v => AzureApp.UsingRuntime(v, language), (r, v) => AzureApp.RuntimeSupported(r, v, language),
                "{0} de {1} aplicativo(s) com " + label + " usam versão obsoleta ou sem suporte.",
                "Os {0} aplicativo(s) com " + label + " usam versão suportada."),
            refs);

    public static IReadOnlyList<KnightIndicatorDefinition> Definitions { get; } = new[]
    {
        // ======== App Service ================================================================================
        RuntimeControl("AK-AZ-APP-001", "java", "Java", AppRefs("2.1.1", "2.2.1", "2.3.1", "2.4.1")),
        RuntimeControl("AK-AZ-APP-002", "python", "Python", AppRefs("2.1.2", "2.2.2", "2.3.2", "2.4.2")),
        RuntimeControl("AK-AZ-APP-003", "php", "PHP", AppRefs("2.1.3", "2.2.3", "2.3.3", "2.4.3")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-004", "Credenciais básicas de publicação habilitadas no App Service", Cloud, SeverityLevel.High,
            "Desabilitar a autenticação básica de publicação (FTP e SCM/Kudu) dos aplicativos, funções e slots e publicar com identidades do Entra ID.",
            "Políticas basicPublishingCredentialsPolicies ftp e scm com allow = false.",
            App("aplicativo do App Service", AzureApp.Sites, (r, _) =>
                {
                    var ftp = r.Bool("ftp:properties.allow");
                    var scm = r.Bool("scm:properties.allow");
                    if (r.Str("ftp:status") is not null || r.Str("scm:status") is not null)
                        return KnightItemCheck.Of(ftp == true || scm == true ? false : null, "políticas de credenciais básicas não lidas por completo", "FTP e SCM sem credenciais básicas");
                    return KnightItemCheck.Of(ftp is null || scm is null ? null : !ftp.Value && !scm.Value,
                        $"credenciais básicas: FTP {(ftp switch { true => "permitidas", false => "bloqueadas", _ => "não informadas" })}, SCM {(scm switch { true => "permitidas", false => "bloqueadas", _ => "não informadas" })}",
                        "FTP e SCM sem credenciais básicas");
                },
                "{0} de {1} aplicativo(s) aceitam publicação com usuário e senha (credenciais básicas).",
                "Os {0} aplicativo(s) bloqueiam as credenciais básicas de publicação."),
            AppRefs("2.1.4", "2.2.4", "2.3.4", "2.4.4")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-005", "FTP sem criptografia aceito no App Service", Cloud, SeverityLevel.High,
            "Definir o estado do FTP como “somente FTPS” ou “desabilitado” em aplicativos, funções e slots.",
            "Configuração ftpsState = FtpsOnly ou Disabled.",
            App("aplicativo do App Service", AzureApp.Sites, (r, _) => AzureApp.ConfigMissing(r, "web", "FTPS somente ou desabilitado")
                    ?? OneOf(r, "web:properties.ftpsState", "estado do FTP", "FtpsOnly ou Disabled", "FtpsOnly", "Disabled"),
                "{0} de {1} aplicativo(s) aceitam FTP sem criptografia.",
                "Os {0} aplicativo(s) aceitam só FTPS ou têm o FTP desabilitado."),
            AppRefs("2.1.5", "2.2.5", "2.3.5", "2.4.5")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-006", "HTTP/2 desabilitado no App Service", Cloud, SeverityLevel.Medium,
            "Habilitar o HTTP/2 em aplicativos, funções e slots.",
            "Configuração http20Enabled = true.",
            App("aplicativo do App Service", AzureApp.Sites, (r, _) => AzureApp.ConfigMissing(r, "web", "HTTP/2 habilitado")
                    ?? Flag(r, "web:properties.http20Enabled", true, "HTTP/2", "habilitado", "desabilitado"),
                "{0} de {1} aplicativo(s) estão com o HTTP/2 desabilitado.",
                "Os {0} aplicativo(s) têm HTTP/2 habilitado."),
            AppRefs("2.1.6", "2.2.6", "2.3.6", "2.4.6")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-007", "App Service aceita HTTP sem redirecionar para HTTPS", Cloud, SeverityLevel.High,
            "Ligar a opção “Somente HTTPS” em aplicativos, funções e slots.",
            "Propriedade httpsOnly = true.",
            App("aplicativo do App Service", AzureApp.Sites, (r, _) => Flag(r, "properties.httpsOnly", true, "somente HTTPS", "ligado", "desligado"),
                "{0} de {1} aplicativo(s) aceitam requisições HTTP sem criptografia.",
                "Os {0} aplicativo(s) atendem somente HTTPS."),
            AppRefs("2.1.7", "2.2.7", "2.3.7", "2.4.7")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-008", "App Service aceita TLS de entrada abaixo de 1.2", Cloud, SeverityLevel.High,
            "Definir a versão mínima de TLS de entrada como 1.2 (ou 1.3) em aplicativos, funções e slots.",
            "Configuração minTlsVersion = 1.2 ou 1.3.",
            App("aplicativo do App Service", AzureApp.Sites, (r, _) => AzureApp.ConfigMissing(r, "web", "TLS mínimo 1.2")
                    ?? OneOf(r, "web:properties.minTlsVersion", "TLS mínimo de entrada", "1.2 ou 1.3", "1.2", "1.3"),
                "{0} de {1} aplicativo(s) aceitam TLS de entrada abaixo de 1.2.",
                "Os {0} aplicativo(s) exigem TLS 1.2 ou superior na entrada."),
            AppRefs("2.1.8", "2.2.8", "2.3.8", "2.4.8")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-009", "App Service sem criptografia TLS de ponta a ponta", Cloud, SeverityLevel.Medium,
            "Habilitar a criptografia de ponta a ponta (endToEndEncryptionEnabled) para que o tráfego entre os front-ends e os workers do App Service também seja cifrado.",
            "Propriedade endToEndEncryptionEnabled = true.",
            App("aplicativo do App Service", AzureApp.Sites,
                (r, _) => Flag(r, "properties.endToEndEncryptionEnabled", true, "criptografia de ponta a ponta", "habilitada", "desabilitada"),
                "{0} de {1} aplicativo(s) estão sem criptografia TLS de ponta a ponta.",
                "Os {0} aplicativo(s) têm criptografia TLS de ponta a ponta."),
            AppRefs("2.1.9", "2.2.9", "2.3.9", "2.4.9")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-010", "Depuração remota ligada no App Service", Cloud, SeverityLevel.High,
            "Desligar a depuração remota em aplicativos, funções e slots (ligar só durante uma sessão de depuração e desligar em seguida).",
            "Configuração remoteDebuggingEnabled = false.",
            App("aplicativo do App Service", AzureApp.Sites, (r, _) => AzureApp.ConfigMissing(r, "web", "depuração remota desligada")
                    ?? Flag(r, "web:properties.remoteDebuggingEnabled", false, "depuração remota", "ligada", "desligada"),
                "{0} de {1} aplicativo(s) estão com a depuração remota ligada.",
                "Os {0} aplicativo(s) estão com a depuração remota desligada."),
            AppRefs("2.1.10", "2.2.10", "2.3.10", "2.4.10")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-011", "App Service não exige certificado de cliente na entrada", Cloud, SeverityLevel.Medium,
            "Exigir certificados de cliente (modo Required) nos aplicativos, funções e slots que atendem clientes conhecidos.",
            "clientCertEnabled = true e clientCertMode = Required.",
            App("aplicativo do App Service", AzureApp.Sites, (r, _) =>
                {
                    var on = r.Bool("properties.clientCertEnabled");
                    var mode = r.Str("properties.clientCertMode");
                    return KnightItemCheck.Of(on is null ? null : on.Value && (mode is null || string.Equals(mode, "Required", StringComparison.OrdinalIgnoreCase)),
                        $"certificado de cliente: {(on == true ? $"ligado (modo {mode ?? "Required"})" : on == false ? "desligado" : "não informado")}",
                        "certificado de cliente exigido (Required)");
                },
                "{0} de {1} aplicativo(s) não exigem certificado de cliente.",
                "Os {0} aplicativo(s) exigem certificado de cliente."),
            AppRefs("2.1.11", "2.2.11", "2.3.11", "2.4.11")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-012", "Autenticação do App Service desligada em aplicativos e slots", Cloud, SeverityLevel.Low,
            "Ligar a autenticação do App Service (Easy Auth) nos aplicativos e slots que exigem usuário autenticado, com o Microsoft Entra ID como provedor.",
            "authsettingsV2 com platform.enabled = true.",
            App("aplicativo web ou slot", v => AzureApp.Sites(v).Where(s => !AzureApp.IsFunction(s)), (r, _) =>
                    r.Str("auth:status") is { } st ? KnightItemCheck.Of(null, $"configuração de autenticação não lida ({st})", "autenticação ligada")
                    : Flag(r, "auth:properties.platform.enabled", true, "autenticação do App Service", "ligada", "desligada"),
                "{0} de {1} aplicativo(s) ou slot(s) estão com a autenticação do App Service desligada.",
                "Os {0} aplicativo(s) e slot(s) têm a autenticação do App Service ligada."),
            Ref(AzureRefs.Azc + "2.1.12#aplicativo"), Ref(AzureRefs.Azc + "2.1.12#slot-de-aplicativo")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-013", "App Service sem identidade gerenciada", Cloud, SeverityLevel.Medium,
            "Atribuir identidade gerenciada aos aplicativos, funções e slots e usá-la para acessar outros recursos, em vez de segredos.",
            "identity.type diferente de None.",
            App("aplicativo do App Service", AzureApp.Sites, (r, _) => ManagedIdentity(r),
                "{0} de {1} aplicativo(s) não têm identidade gerenciada.",
                "Os {0} aplicativo(s) têm identidade gerenciada."),
            Ref(AzureRefs.Azc + "2.1.13#aplicativo"), Ref(AzureRefs.Azc + "2.2.12#slot-de-aplicativo"), Ref(AzureRefs.Azc + "2.3.12#funcao"),
            Ref(AzureRefs.Azc + "2.4.11#slot-de-funcao-identidade-gerenciada")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-014", "Acesso público de rede habilitado no App Service", Cloud, SeverityLevel.Medium,
            "Desabilitar o acesso público de rede dos aplicativos, funções e slots que só devem ser acessados por redes privadas.",
            "Propriedade publicNetworkAccess = Disabled.",
            App("aplicativo do App Service", AzureApp.Sites, (r, _) =>
                {
                    var p = r.Str("properties.publicNetworkAccess");
                    if (string.IsNullOrEmpty(p))
                    {
                        var pe = AzureApp.PrivateEndpoint(r);
                        return pe.Verdict == KnightItemVerdict.NonCompliant
                            ? KnightItemCheck.Of(false, "acesso público não definido e sem endpoint privado: o aplicativo atende pela rede pública", "acesso público de rede: desabilitado")
                            : KnightItemCheck.Of(null, "acesso público não definido (o efeito depende dos endpoints privados)", "acesso público de rede: desabilitado");
                    }
                    return PublicAccessDisabled(r);
                },
                "{0} de {1} aplicativo(s) aceitam acesso pela rede pública.",
                "Os {0} aplicativo(s) têm o acesso público de rede desabilitado."),
            AppRefs("2.1.14", "2.2.13", "2.3.13", "2.4.12")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-015", "Aplicativos web sem endpoint privado", Cloud, SeverityLevel.Medium,
            "Acessar os aplicativos web por endpoint privado (Private Link).",
            "Ao menos uma conexão de endpoint privado aprovada em cada aplicativo web.",
            App("aplicativo web", v => v.OfType("Microsoft.Web/sites").Where(s => !AzureApp.IsFunction(s)), (r, _) => AzureApp.PrivateEndpoint(r),
                "{0} de {1} aplicativo(s) web não têm endpoint privado aprovado.",
                "Os {0} aplicativo(s) web têm endpoint privado aprovado."),
            Ref(AzureRefs.Azc + "2.1.16#aplicativo")),

        // [AEGIS-KNIGHT-COVERAGE-04] Antes pesquisa pendente: o grupo de zonas DNS de cada endpoint privado é lido em
        // Microsoft.Network/privateEndpoints/privateDnsZoneGroups (versão estável) e ligado ao aplicativo pelo id do endpoint.
        Control(KnightService.AzureAppService, "AK-AZ-APP-025", "Endpoints privados do App Service sem zona DNS privada", Cloud, SeverityLevel.Medium,
            "Associar a zona DNS privada privatelink.azurewebsites.net (grupo de zonas DNS) a cada endpoint privado dos aplicativos.",
            "Cada endpoint privado aprovado do aplicativo tem grupo de zonas DNS com a zona privatelink.azurewebsites.net.",
            new AzureCheck(KnightCapability.AzureAppService, "aplicativo com endpoint privado",
                v => AzureApp.Sites(v).Where(AzureApp.HasApprovedPrivateEndpoint),
                (r, v) => PrivateDnsZone(AzureApp.PrivateEndpointConnections(r), v, "privatelink.azurewebsites.net"),
                "{0} de {1} aplicativo(s) com endpoint privado não têm a zona DNS privada do App Service.",
                "Os {0} aplicativo(s) com endpoint privado têm a zona DNS privada do App Service.",
                new[] { KnightCapability.AzureNetworking }),
            Ref(AzureRefs.Azc + "2.1.17", KnightReferenceMatch.Partial,
                "Avalia as zonas DNS privadas do Azure ligadas aos endpoints; um servidor DNS próprio da organização, que a referência também aceita, não é visível pelo Resource Manager.")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-026", "Planos do App Service sem suporte a endpoint privado", Cloud, SeverityLevel.Low,
            "Hospedar os aplicativos que precisam de acesso privado em planos Basic ou superiores (ou Functions Premium/Flex Consumption).",
            "Camada do plano (sku.tier) entre as que a Microsoft documenta com suporte a endpoint privado.",
            App("plano do App Service", v => v.OfType("Microsoft.Web/serverfarms"), (r, _) => AzureApp.PlanSupportsPrivateEndpoint(r),
                "{0} de {1} plano(s) do App Service estão em camada sem suporte a endpoint privado.",
                "Os {0} plano(s) do App Service estão em camada com suporte a endpoint privado."),
            Ref(AzureRefs.Azc + "s/n#plano-endpoint-privado")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-016", "App Service sem integração com rede virtual", Cloud, SeverityLevel.Medium,
            "Integrar os aplicativos, funções e slots a uma sub-rede de rede virtual para o tráfego de saída.",
            "Propriedade virtualNetworkSubnetId definida.",
            App("aplicativo do App Service", AzureApp.Sites, (r, _) =>
                {
                    var subnet = r.Str("properties.virtualNetworkSubnetId");
                    return KnightItemCheck.Of(!string.IsNullOrEmpty(subnet), string.IsNullOrEmpty(subnet) ? "sem integração com rede virtual" : $"integrado à sub-rede {subnet.Split('/').Last()}",
                        "integrado a uma sub-rede");
                },
                "{0} de {1} aplicativo(s) não estão integrados a uma rede virtual.",
                "Os {0} aplicativo(s) estão integrados a uma rede virtual."),
            AppRefs("2.1.18", "2.2.14", "2.3.14", "2.4.13")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-017", "Imagens de contêiner e conteúdo do App Service fora da rede virtual", Cloud, SeverityLevel.Medium,
            "Rotear pela rede virtual o download de imagens de contêiner (vnetImagePullEnabled) e o acesso ao compartilhamento de conteúdo (vnetContentShareEnabled).",
            "vnetImagePullEnabled = true e vnetContentShareEnabled = true.",
            App("aplicativo do App Service", AzureApp.Sites, (r, _) =>
                {
                    var img = r.Bool("properties.vnetImagePullEnabled");
                    var content = r.Bool("properties.vnetContentShareEnabled");
                    return KnightItemCheck.Of(img is null && content is null ? null : img == true && content == true,
                        $"imagens pela VNet: {YesNo(img)}; conteúdo pela VNet: {YesNo(content)}", "imagens e conteúdo roteados pela rede virtual");
                },
                "{0} de {1} aplicativo(s) buscam imagens ou conteúdo fora da rede virtual.",
                "Os {0} aplicativo(s) roteiam imagens e conteúdo pela rede virtual."),
            AppRefs("2.1.19", "2.2.15", "2.3.15", "2.4.14")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-018", "Tráfego de saída do App Service não roteado pela rede virtual", Cloud, SeverityLevel.Medium,
            "Ligar o roteamento de todo o tráfego de saída pela rede virtual (vnetRouteAllEnabled) nos aplicativos integrados.",
            "vnetRouteAllEnabled = true (no site ou na configuração).",
            App("aplicativo do App Service", AzureApp.Sites, (r, _) =>
                {
                    var site = r.Bool("properties.vnetRouteAllEnabled");
                    var cfg = r.Bool("web:properties.vnetRouteAllEnabled");
                    bool? on = site == true || cfg == true ? true : site is null && cfg is null ? null : false;
                    return KnightItemCheck.Of(on, $"todo o tráfego pela VNet: {YesNo(on)}", "todo o tráfego de saída pela rede virtual");
                },
                "{0} de {1} aplicativo(s) enviam parte do tráfego de saída fora da rede virtual.",
                "Os {0} aplicativo(s) roteiam todo o tráfego de saída pela rede virtual."),
            AppRefs("2.1.20", "2.2.16", "2.3.16", "2.4.15")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-019", "CORS do App Service aceita qualquer origem", Cloud, SeverityLevel.Medium,
            "Substituir a origem “*” do CORS por origens nominais em aplicativos, funções e slots.",
            "Lista cors.allowedOrigins sem “*”.",
            App("aplicativo do App Service", AzureApp.Sites, (r, _) =>
                {
                    if (AzureApp.ConfigMissing(r, "web", "CORS sem “*”") is { } miss) return miss;
                    var origins = r.Strings("web:properties.cors.allowedOrigins");
                    return KnightItemCheck.Of(!origins.Contains("*"), origins.Count == 0 ? "CORS sem origens configuradas" : $"origens: {List(origins, 4)}", "CORS sem “*”");
                },
                "{0} de {1} aplicativo(s) aceitam requisições de navegador de qualquer origem (CORS “*”).",
                "Nenhum dos {0} aplicativo(s) aceita CORS de qualquer origem."),
            AppRefs("2.1.21", "2.2.17", "2.3.17", "2.4.16")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-020", "Ambiente do App Service com balanceador externo", Cloud, SeverityLevel.Low,
            "Usar balanceador de carga interno (ILB) nos ambientes do App Service que hospedam aplicações internas.",
            "internalLoadBalancingMode diferente de None.",
            App("ambiente do App Service", v => v.OfType("Microsoft.Web/hostingEnvironments"), (r, _) =>
                {
                    var m = r.Str("properties.internalLoadBalancingMode");
                    return KnightItemCheck.Of(m is null ? null : !string.Equals(m, "None", StringComparison.OrdinalIgnoreCase), $"balanceamento: {m ?? "não informado"}", "balanceador interno (Web, Publishing)");
                },
                "{0} de {1} ambiente(s) do App Service expõem os aplicativos por balanceador externo.",
                "Os {0} ambiente(s) do App Service usam balanceador interno."),
            Ref(AzureRefs.Azc + "2.6")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-021", "Ambiente do App Service em versão anterior à v3", Cloud, SeverityLevel.High,
            "Migrar os ambientes do App Service v1/v2 (aposentados) para o ASE v3.",
            "Tipo do ambiente ASEV3.",
            App("ambiente do App Service", v => v.OfType("Microsoft.Web/hostingEnvironments"), (r, _) =>
                    KnightItemCheck.Of(r.Kind is null ? null : r.Kind.StartsWith("ASEV3", StringComparison.OrdinalIgnoreCase), $"tipo: {r.Kind ?? "não informado"}", "ASEV3"),
                "{0} de {1} ambiente(s) do App Service estão em versão anterior à v3.",
                "Os {0} ambiente(s) do App Service são v3."),
            Ref(AzureRefs.Azc + "2.7")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-022", "Ambiente do App Service sem criptografia interna", Cloud, SeverityLevel.Medium,
            "Habilitar a criptografia interna (InternalEncryption = true) nos ambientes do App Service.",
            "clusterSettings com InternalEncryption = true.",
            App("ambiente do App Service", v => v.OfType("Microsoft.Web/hostingEnvironments"), (r, _) =>
                {
                    var val = AzureApp.ClusterSetting(r, "InternalEncryption");
                    return KnightItemCheck.Of(string.Equals(val, "true", StringComparison.OrdinalIgnoreCase), $"InternalEncryption: {val ?? "não definido"}", "InternalEncryption = true");
                },
                "{0} de {1} ambiente(s) do App Service estão sem criptografia interna.",
                "Os {0} ambiente(s) do App Service têm criptografia interna."),
            Ref(AzureRefs.Azc + "2.8")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-023", "Ambiente do App Service aceita TLS 1.0 e 1.1", Cloud, SeverityLevel.High,
            "Desabilitar o TLS 1.0 e 1.1 (DisableTls1.0 = 1) nos ambientes do App Service.",
            "clusterSettings com DisableTls1.0 = 1.",
            App("ambiente do App Service", v => v.OfType("Microsoft.Web/hostingEnvironments"), (r, _) =>
                {
                    var val = AzureApp.ClusterSetting(r, "DisableTls1.0");
                    return KnightItemCheck.Of(val == "1", $"DisableTls1.0: {val ?? "não definido"}", "DisableTls1.0 = 1");
                },
                "{0} de {1} ambiente(s) do App Service aceitam TLS 1.0 e 1.1.",
                "Os {0} ambiente(s) do App Service recusam TLS 1.0 e 1.1."),
            Ref(AzureRefs.Azc + "2.9")),

        Control(KnightService.AzureAppService, "AK-AZ-APP-024", "Ambiente do App Service sem ordem de cifras TLS definida", Cloud, SeverityLevel.Medium,
            "Definir a ordem das cifras TLS de front-end (FrontEndSSLCipherSuiteOrder) com cifras fortes nos ambientes do App Service.",
            "clusterSettings com FrontEndSSLCipherSuiteOrder definido.",
            App("ambiente do App Service", v => v.OfType("Microsoft.Web/hostingEnvironments"), (r, _) =>
                {
                    var val = AzureApp.ClusterSetting(r, "FrontEndSSLCipherSuiteOrder");
                    return KnightItemCheck.Of(!string.IsNullOrWhiteSpace(val), string.IsNullOrWhiteSpace(val) ? "ordem de cifras não definida" : $"ordem de cifras: {Trim(val, 120)}",
                        "FrontEndSSLCipherSuiteOrder definido");
                },
                "{0} de {1} ambiente(s) do App Service não definem a ordem das cifras TLS.",
                "Os {0} ambiente(s) do App Service definem a ordem das cifras TLS."),
            Ref(AzureRefs.Azc + "2.10")),

        // ======== Computação =================================================================================
        Control(KnightService.AzureCompute, "AK-AZ-CMP-001", "Container Instances fora de rede virtual privada", Cloud, SeverityLevel.Medium,
            "Implantar os grupos de contêineres em sub-redes de rede virtual, sem IP público.",
            "Grupo de contêineres com subnetIds definido e sem IP público.",
            Compute("grupo de contêineres", v => v.OfType("Microsoft.ContainerInstance/containerGroups"), (r, _) =>
                {
                    var subnets = r.Items("properties.subnetIds").Count;
                    var ip = r.Str("properties.ipAddress.type");
                    return KnightItemCheck.Of(subnets > 0 && !string.Equals(ip, "Public", StringComparison.OrdinalIgnoreCase),
                        $"{(subnets > 0 ? "em rede virtual" : "fora de rede virtual")}; IP {ip ?? "nenhum"}", "em rede virtual, sem IP público");
                },
                "{0} de {1} grupo(s) de contêineres estão fora de rede virtual privada.",
                "Os {0} grupo(s) de contêineres estão em rede virtual privada."),
            Ref(AzureRefs.Azc + "3.1")),

        Control(KnightService.AzureCompute, "AK-AZ-CMP-002", "Container Instances sem identidade gerenciada", Cloud, SeverityLevel.Medium,
            "Atribuir identidade gerenciada aos grupos de contêineres que acessam outros recursos.",
            "identity.type diferente de None.",
            Compute("grupo de contêineres", v => v.OfType("Microsoft.ContainerInstance/containerGroups"), (r, _) => ManagedIdentity(r),
                "{0} de {1} grupo(s) de contêineres não têm identidade gerenciada.",
                "Os {0} grupo(s) de contêineres têm identidade gerenciada."),
            Ref(AzureRefs.Azc + "3.2")),

        Control(KnightService.AzureCompute, "AK-AZ-CMP-003", "Contas do Batch sem chave gerenciada pelo cliente", Cloud, SeverityLevel.Medium,
            "Cifrar as contas do Batch com chave gerenciada pelo cliente no Key Vault.",
            "encryption.keySource = Microsoft.KeyVault.",
            Compute("conta do Batch", v => v.OfType("Microsoft.Batch/batchAccounts"),
                (r, _) => OneOf(r, "properties.encryption.keySource", "origem da chave", "Microsoft.KeyVault", "Microsoft.KeyVault"),
                "{0} de {1} conta(s) do Batch usam chave gerenciada pela Microsoft.",
                "As {0} conta(s) do Batch usam chave gerenciada pelo cliente."),
            Ref(AzureRefs.Azc + "15.1")),

        Control(KnightService.AzureCompute, "AK-AZ-CMP-004", "Pools do Batch sem criptografia de disco", Cloud, SeverityLevel.Medium,
            "Habilitar a criptografia de disco (disco do SO e temporário) na configuração das máquinas dos pools do Batch.",
            "diskEncryptionConfiguration.targets não vazio em cada pool.",
            Compute("pool do Batch",
                v => v.OfType("Microsoft.Batch/batchAccounts").SelectMany(a => a.Has("pools:items")
                    ? a.Items("pools:items").Select(p => AzureView.Derived(a, $"{a.Id}/pools/{AzureJson.Str(p, "name")}", "Microsoft.Batch/batchAccounts/pools",
                        $"{a.Name}/{AzureJson.Str(p, "name")}", p))
                    : new[] { AzureView.Derived(a, $"{a.Id}/pools#nao-lidos", "Microsoft.Batch/batchAccounts/pools", $"{a.Name} (pools não lidos)") }),
                (p, _) =>
                {
                    if (p.Get("item") is not { } e) return KnightItemCheck.Of(null, "pools não lidos nesta coleta", "criptografia de disco habilitada");
                    var targets = AzureJson.Items(e, "properties.deploymentConfiguration.virtualMachineConfiguration.diskEncryptionConfiguration.targets")
                        .Select(t => t.GetString() ?? "?").ToList();
                    return KnightItemCheck.Of(targets.Count > 0, targets.Count > 0 ? $"discos cifrados: {List(targets)}" : "sem criptografia de disco", "criptografia de disco habilitada");
                },
                "{0} de {1} pool(s) do Batch estão sem criptografia de disco.",
                "Os {0} pool(s) do Batch têm criptografia de disco."),
            Ref(AzureRefs.Azc + "15.2")),

        Control(KnightService.AzureCompute, "AK-AZ-CMP-005", "Contas do Batch aceitam autenticação por chave compartilhada", Cloud, SeverityLevel.Medium,
            "Remover o modo SharedKey dos modos de autenticação das contas do Batch e autenticar pelo Microsoft Entra ID.",
            "allowedAuthenticationModes sem SharedKey.",
            Compute("conta do Batch", v => v.OfType("Microsoft.Batch/batchAccounts"), (r, _) =>
                {
                    var modes = r.Strings("properties.allowedAuthenticationModes");
                    return KnightItemCheck.Of(r.Has("properties.allowedAuthenticationModes") ? !modes.Contains("SharedKey", StringComparer.OrdinalIgnoreCase) : null,
                        $"modos: {List(modes)}", "sem SharedKey");
                },
                "{0} de {1} conta(s) do Batch aceitam autenticação por chave compartilhada.",
                "As {0} conta(s) do Batch não aceitam chave compartilhada."),
            Ref(AzureRefs.Azc + "15.3")),

        Control(KnightService.AzureCompute, "AK-AZ-CMP-006", "Contas do Batch sem endpoint privado", Cloud, SeverityLevel.Medium,
            "Acessar as contas do Batch por endpoint privado (Private Link).",
            "Ao menos uma conexão de endpoint privado aprovada em cada conta.",
            Compute("conta do Batch", v => v.OfType("Microsoft.Batch/batchAccounts"), (r, _) => PrivateEndpoint(r),
                "{0} de {1} conta(s) do Batch não têm endpoint privado aprovado.",
                "As {0} conta(s) do Batch têm endpoint privado aprovado."),
            Ref(AzureRefs.Azc + "15.4")),

        Control(KnightService.AzureCompute, "AK-AZ-CMP-007", "Acesso público de rede habilitado nas contas do Batch", Cloud, SeverityLevel.Medium,
            "Desabilitar o acesso público de rede das contas do Batch.",
            "publicNetworkAccess = Disabled.",
            Compute("conta do Batch", v => v.OfType("Microsoft.Batch/batchAccounts"), (r, _) => PublicAccessDisabled(r),
                "{0} de {1} conta(s) do Batch aceitam acesso pela rede pública.",
                "As {0} conta(s) do Batch têm o acesso público de rede desabilitado."),
            Ref(AzureRefs.Azc + "15.5")),

        Control(KnightService.AzureCompute, "AK-AZ-CMP-008", "Máquinas virtuais com discos não gerenciados", Cloud, SeverityLevel.Medium,
            "Migrar os discos das máquinas virtuais para discos gerenciados.",
            "Disco do SO de cada máquina virtual é um disco gerenciado.",
            Compute("máquina virtual", v => v.OfType("Microsoft.Compute/virtualMachines"), (r, _) =>
                {
                    var managed = r.Has("properties.storageProfile.osDisk.managedDisk.id");
                    var vhd = r.Has("properties.storageProfile.osDisk.vhd.uri");
                    return KnightItemCheck.Of(managed ? true : vhd ? false : null, managed ? "disco do SO gerenciado" : vhd ? "disco do SO em VHD (não gerenciado)" : "disco do SO não informado",
                        "disco gerenciado");
                },
                "{0} de {1} máquina(s) virtual(is) usam discos não gerenciados.",
                "As {0} máquina(s) virtual(is) usam discos gerenciados."),
            Ref(AzureRefs.Azc + "20.1")),

        Control(KnightService.AzureCompute, "AK-AZ-CMP-009", "Discos de máquinas virtuais sem chave gerenciada pelo cliente", Cloud, SeverityLevel.Medium,
            "Cifrar os discos de SO e de dados anexados com chave gerenciada pelo cliente (conjunto de criptografia de disco).",
            "Discos anexados com encryption.type EncryptionAtRestWithCustomerKey ou EncryptionAtRestWithPlatformAndCustomerKeys.",
            Compute("disco anexado", v => v.OfType("Microsoft.Compute/disks").Where(d => d.Has("managedBy")), (d, _) => AzureCmp.Cmk(d),
                "{0} de {1} disco(s) anexado(s) usam chave gerenciada pela plataforma.",
                "Os {0} disco(s) anexado(s) usam chave gerenciada pelo cliente."),
            Ref(AzureRefs.Azc + "20.2")),

        Control(KnightService.AzureCompute, "AK-AZ-CMP-010", "Discos não anexados sem chave gerenciada pelo cliente", Cloud, SeverityLevel.Medium,
            "Cifrar os discos não anexados com chave gerenciada pelo cliente, ou excluí-los quando não forem mais necessários.",
            "Discos sem máquina associada com criptografia por chave do cliente.",
            Compute("disco não anexado", v => v.OfType("Microsoft.Compute/disks").Where(d => !d.Has("managedBy")), (d, _) => AzureCmp.Cmk(d),
                "{0} de {1} disco(s) não anexado(s) usam chave gerenciada pela plataforma.",
                "Os {0} disco(s) não anexado(s) usam chave gerenciada pelo cliente."),
            Ref(AzureRefs.Azc + "20.3")),

        Control(KnightService.AzureCompute, "AK-AZ-CMP-011", "Discos gerenciados acessíveis de todas as redes", Cloud, SeverityLevel.Medium,
            "Restringir o acesso de rede dos discos (exportação e importação) a endpoints privados ou desabilitá-lo.",
            "networkAccessPolicy diferente de AllowAll, ou publicNetworkAccess = Disabled.",
            Compute("disco gerenciado", v => v.OfType("Microsoft.Compute/disks"), (d, _) =>
                {
                    var policy = d.Str("properties.networkAccessPolicy");
                    var pub = d.Str("properties.publicNetworkAccess");
                    bool? ok = string.Equals(pub, "Disabled", StringComparison.OrdinalIgnoreCase) ? true
                        : policy is null ? null : !string.Equals(policy, "AllowAll", StringComparison.OrdinalIgnoreCase);
                    return KnightItemCheck.Of(ok, $"política de acesso de rede: {policy ?? "não informada"}; acesso público: {pub ?? "não informado"}",
                        "sem acesso de todas as redes");
                },
                "{0} de {1} disco(s) aceitam exportação e importação de todas as redes.",
                "Os {0} disco(s) não aceitam acesso de todas as redes."),
            Ref(AzureRefs.Azc + "20.4")),

        Control(KnightService.AzureCompute, "AK-AZ-CMP-012", "Discos sem autenticação do Entra ID para exportação de dados", Cloud, SeverityLevel.Medium,
            "Exigir autenticação do Microsoft Entra ID (dataAccessAuthMode = AzureActiveDirectory) para exportar ou importar dados dos discos.",
            "dataAccessAuthMode = AzureActiveDirectory.",
            Compute("disco gerenciado", v => v.OfType("Microsoft.Compute/disks"), (d, _) =>
                {
                    var m = d.Str("properties.dataAccessAuthMode");
                    return KnightItemCheck.Of(string.Equals(m, "AzureActiveDirectory", StringComparison.OrdinalIgnoreCase),
                        $"modo de autenticação de acesso a dados: {m ?? "não definido (SAS sem identidade)"}", "AzureActiveDirectory");
                },
                "{0} de {1} disco(s) permitem exportar dados sem autenticação do Entra ID.",
                "Os {0} disco(s) exigem autenticação do Entra ID para exportar dados."),
            Ref(AzureRefs.Azc + "20.5")),

        Control(KnightService.AzureCompute, "AK-AZ-CMP-013", "Máquinas virtuais sem extensão de proteção de endpoint reconhecida", Cloud, SeverityLevel.Medium,
            "Instalar a proteção de endpoint nas máquinas virtuais (Microsoft Defender para Endpoint pelo Defender para Servidores, ou o Antimalware da Microsoft).",
            "Extensão MDE.Windows/MDE.Linux ou IaaSAntimalware presente e provisionada.",
            Compute("máquina virtual", v => v.OfType("Microsoft.Compute/virtualMachines"), (r, _) => AzureCmp.EndpointProtection(r),
                "{0} de {1} máquina(s) virtual(is) não têm proteção de endpoint reconhecida.",
                "As {0} máquina(s) virtual(is) têm extensão de proteção de endpoint."),
            Ref(AzureRefs.Azc + "20.7", KnightReferenceMatch.Partial,
                "Reconhece as extensões MDE.Windows, MDE.Linux e IaaSAntimalware; uma solução de terceiros instalada sem extensão não é visível pelo Resource Manager e deixa a máquina como não avaliada.")),

        Control(KnightService.AzureCompute, "AK-AZ-CMP-014", "VHDs não gerenciados sem criptografia", Cloud, SeverityLevel.Medium,
            "Cifrar os discos VHD não gerenciados (Azure Disk Encryption) ou migrá-los para discos gerenciados.",
            "Máquinas com disco do SO em VHD têm encryptionSettings habilitado.",
            Compute("máquina virtual com VHD", v => v.OfType("Microsoft.Compute/virtualMachines").Where(m => m.Has("properties.storageProfile.osDisk.vhd.uri")),
                (r, _) => Flag(r, "properties.storageProfile.osDisk.encryptionSettings.enabled", true, "criptografia do VHD", "habilitada", "desabilitada"),
                "{0} de {1} máquina(s) com VHD não gerenciado não cifram o disco.",
                "As {0} máquina(s) com VHD não gerenciado cifram o disco."),
            Ref(AzureRefs.Azc + "20.8")),

        Control(KnightService.AzureCompute, "AK-AZ-CMP-015", "Máquinas virtuais sem inicialização confiável", Cloud, SeverityLevel.Medium,
            "Usar inicialização confiável (Trusted Launch, com inicialização segura e vTPM) ou VM confidencial nas máquinas virtuais.",
            "securityProfile.securityType = TrustedLaunch ou ConfidentialVM.",
            Compute("máquina virtual", v => v.OfType("Microsoft.Compute/virtualMachines"), (r, _) =>
                {
                    var t = r.Str("properties.securityProfile.securityType");
                    return KnightItemCheck.Of(t is "TrustedLaunch" or "ConfidentialVM", $"tipo de segurança: {t ?? "padrão (sem inicialização confiável)"}", "TrustedLaunch ou ConfidentialVM");
                },
                "{0} de {1} máquina(s) virtual(is) estão sem inicialização confiável.",
                "As {0} máquina(s) virtual(is) usam inicialização confiável."),
            Ref(AzureRefs.Azc + "20.10")),

        Control(KnightService.AzureCompute, "AK-AZ-CMP-016", "Máquinas virtuais sem criptografia no host", Cloud, SeverityLevel.Medium,
            "Habilitar a criptografia no host (encryptionAtHost) nas máquinas virtuais.",
            "securityProfile.encryptionAtHost = true.",
            Compute("máquina virtual", v => v.OfType("Microsoft.Compute/virtualMachines"),
                (r, _) => Flag(r, "properties.securityProfile.encryptionAtHost", true, "criptografia no host", "habilitada", "desabilitada",
                    documentedDefault: false, defaultSource: "ausente = desabilitada"),
                "{0} de {1} máquina(s) virtual(is) estão sem criptografia no host.",
                "As {0} máquina(s) virtual(is) têm criptografia no host."),
            Ref(AzureRefs.Azc + "20.11")),

        Control(KnightService.AzureCompute, "AK-AZ-CMP-017", "Endpoints privados do Batch sem zona DNS privada", Cloud, SeverityLevel.Medium,
            "Associar a zona DNS privada privatelink.batch.azure.com (grupo de zonas DNS) a cada endpoint privado das contas do Batch.",
            "Cada endpoint privado aprovado da conta do Batch tem grupo de zonas DNS com a zona privatelink.batch.azure.com.",
            new AzureCheck(KnightCapability.AzureCompute, "conta do Batch com endpoint privado",
                v => v.OfType("Microsoft.Batch/batchAccounts").Where(b => ApprovedPrivateEndpoints(b) > 0),
                (r, v) => PrivateDnsZone(r.Items("properties.privateEndpointConnections"), v, "privatelink.batch.azure.com"),
                "{0} de {1} conta(s) do Batch com endpoint privado não têm a zona DNS privada do Batch.",
                "As {0} conta(s) do Batch com endpoint privado têm a zona DNS privada do Batch.",
                new[] { KnightCapability.AzureNetworking }),
            Ref(AzureRefs.Azc + "15.6")),

        // ======== Bancos de dados: Cache for Redis e Redis Enterprise =========================================
        Control(KnightService.AzureDatabases, "AK-AZ-DB-001", "Cache for Redis sem autenticação do Entra ID", Cloud, SeverityLevel.Medium,
            "Habilitar a autenticação do Microsoft Entra ID (aad-enabled) nos caches do Redis.",
            "redisConfiguration.aad-enabled = true.",
            Db("cache do Redis", AzureDb.Redis, (r, _) => Flag(r, "properties.redisConfiguration.aad-enabled", true, "autenticação do Entra ID", "habilitada", "desabilitada"),
                "{0} de {1} cache(s) do Redis estão sem autenticação do Entra ID.",
                "Os {0} cache(s) do Redis têm autenticação do Entra ID."),
            Ref(AzureRefs.Azd + "2.1")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-002", "Cache for Redis aceita conexões sem SSL", Cloud, SeverityLevel.High,
            "Desabilitar a porta sem SSL (enableNonSslPort = false) nos caches do Redis.",
            "enableNonSslPort = false.",
            Db("cache do Redis", AzureDb.Redis, (r, _) => Flag(r, "properties.enableNonSslPort", false, "porta sem SSL", "habilitada", "desabilitada",
                    documentedDefault: false, defaultSource: "padrão: porta sem SSL desabilitada"),
                "{0} de {1} cache(s) do Redis aceitam conexões sem SSL.",
                "Os {0} cache(s) do Redis aceitam só conexões SSL."),
            Ref(AzureRefs.Azd + "2.2")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-003", "Cache for Redis aceita TLS abaixo de 1.2", Cloud, SeverityLevel.High,
            "Definir a versão mínima de TLS dos caches do Redis como 1.2.",
            "minimumTlsVersion = 1.2.",
            Db("cache do Redis", AzureDb.Redis, (r, _) => OneOf(r, "properties.minimumTlsVersion", "TLS mínimo", "1.2", "1.2", "1.3"),
                "{0} de {1} cache(s) do Redis aceitam TLS abaixo de 1.2.",
                "Os {0} cache(s) do Redis exigem TLS 1.2."),
            Ref(AzureRefs.Azd + "2.3")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-004", "Redis Enterprise aceita TLS abaixo de 1.2", Cloud, SeverityLevel.High,
            "Definir a versão mínima de TLS dos clusters do Redis Enterprise como 1.2.",
            "minimumTlsVersion = 1.2 no cluster.",
            Db("cluster do Redis Enterprise", AzureDb.RedisEnterprise, (r, _) => OneOf(r, "properties.minimumTlsVersion", "TLS mínimo", "1.2", "1.2", "1.3"),
                "{0} de {1} cluster(s) do Redis Enterprise aceitam TLS abaixo de 1.2.",
                "Os {0} cluster(s) do Redis Enterprise exigem TLS 1.2."),
            Ref(AzureRefs.Azd + "2.3#redis-enterprise")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-005", "Cache for Redis sem identidade gerenciada do sistema", Cloud, SeverityLevel.Medium,
            "Atribuir identidade gerenciada pelo sistema aos caches do Redis.",
            "identity.type contendo SystemAssigned.",
            Db("cache do Redis", AzureDb.Redis, (r, _) => ManagedIdentity(r, systemAssignedOnly: true),
                "{0} de {1} cache(s) do Redis não têm identidade gerenciada do sistema.",
                "Os {0} cache(s) do Redis têm identidade gerenciada do sistema."),
            Ref(AzureRefs.Azd + "2.5")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-006", "Redis Enterprise sem identidade gerenciada", Cloud, SeverityLevel.Medium,
            "Atribuir identidade gerenciada aos clusters do Redis Enterprise.",
            "identity.type diferente de None.",
            Db("cluster do Redis Enterprise", AzureDb.RedisEnterprise, (r, _) => ManagedIdentity(r),
                "{0} de {1} cluster(s) do Redis Enterprise não têm identidade gerenciada.",
                "Os {0} cluster(s) do Redis Enterprise têm identidade gerenciada."),
            Ref(AzureRefs.Azd + "2.5#redis-enterprise")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-007", "Acesso público de rede habilitado no Cache for Redis", Cloud, SeverityLevel.High,
            "Desabilitar o acesso público de rede dos caches do Redis e usar Private Link.",
            "publicNetworkAccess = Disabled.",
            Db("cache do Redis", AzureDb.Redis, (r, _) => PublicAccessDisabled(r),
                "{0} de {1} cache(s) do Redis aceitam acesso pela rede pública.",
                "Os {0} cache(s) do Redis têm o acesso público de rede desabilitado."),
            Ref(AzureRefs.Azd + "2.6")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-008", "Acesso público de rede habilitado no Redis Enterprise", Cloud, SeverityLevel.High,
            "Desabilitar o acesso público de rede dos clusters do Redis Enterprise e usar Private Link.",
            "publicNetworkAccess = Disabled no cluster.",
            Db("cluster do Redis Enterprise", AzureDb.RedisEnterprise, (r, _) => PublicAccessDisabled(r),
                "{0} de {1} cluster(s) do Redis Enterprise aceitam acesso pela rede pública.",
                "Os {0} cluster(s) do Redis Enterprise têm o acesso público de rede desabilitado."),
            Ref(AzureRefs.Azd + "2.6#redis-enterprise")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-009", "Cache for Redis sem Private Link", Cloud, SeverityLevel.Medium,
            "Acessar os caches do Redis por endpoint privado (Private Link).",
            "Ao menos uma conexão de endpoint privado aprovada.",
            Db("cache do Redis", AzureDb.Redis, (r, _) => PrivateEndpoint(r),
                "{0} de {1} cache(s) do Redis não têm endpoint privado aprovado.",
                "Os {0} cache(s) do Redis têm endpoint privado aprovado."),
            Ref(AzureRefs.Azd + "2.7")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-010", "Redis Enterprise sem Private Link", Cloud, SeverityLevel.Medium,
            "Acessar os clusters do Redis Enterprise por endpoint privado (Private Link).",
            "Ao menos uma conexão de endpoint privado aprovada no cluster.",
            Db("cluster do Redis Enterprise", AzureDb.RedisEnterprise, (r, _) => PrivateEndpoint(r),
                "{0} de {1} cluster(s) do Redis Enterprise não têm endpoint privado aprovado.",
                "Os {0} cluster(s) do Redis Enterprise têm endpoint privado aprovado."),
            Ref(AzureRefs.Azd + "2.7#redis-enterprise")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-011", "Redis Enterprise sem chave gerenciada pelo cliente", Cloud, SeverityLevel.Medium,
            "Cifrar os clusters do Redis Enterprise com chave gerenciada pelo cliente.",
            "encryption.customerManagedKeyEncryption.keyEncryptionKeyUrl definido.",
            Db("cluster do Redis Enterprise", AzureDb.RedisEnterprise, (r, _) =>
                {
                    var url = r.Has("properties.encryption.customerManagedKeyEncryption.keyEncryptionKeyUrl");
                    return KnightItemCheck.Of(url, url ? "chave do cliente configurada" : "chave gerenciada pela Microsoft", "chave gerenciada pelo cliente");
                },
                "{0} de {1} cluster(s) do Redis Enterprise usam chave gerenciada pela Microsoft.",
                "Os {0} cluster(s) do Redis Enterprise usam chave gerenciada pelo cliente."),
            Ref(AzureRefs.Azd + "2.8")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-012", "Cache for Redis aceita autenticação por chave de acesso", Cloud, SeverityLevel.High,
            "Desabilitar a autenticação por chave de acesso (disableAccessKeyAuthentication = true) nos caches do Redis, usando o Entra ID.",
            "disableAccessKeyAuthentication = true.",
            Db("cache do Redis", AzureDb.Redis, (r, _) => Flag(r, "properties.disableAccessKeyAuthentication", true, "autenticação por chave de acesso", "desabilitada", "habilitada",
                    documentedDefault: false, defaultSource: "padrão: chaves de acesso habilitadas"),
                "{0} de {1} cache(s) do Redis aceitam autenticação por chave de acesso.",
                "Os {0} cache(s) do Redis não aceitam chave de acesso."),
            Ref(AzureRefs.Azd + "2.9")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-013", "Redis Enterprise aceita autenticação por chave de acesso", Cloud, SeverityLevel.High,
            "Desabilitar a autenticação por chave de acesso (accessKeysAuthentication = Disabled) nos bancos dos clusters do Redis Enterprise.",
            "Cada banco do cluster com accessKeysAuthentication = Disabled.",
            Db("cluster do Redis Enterprise", AzureDb.RedisEnterprise, (r, _) =>
                {
                    if (r.Str("databases:status") is { } st) return KnightItemCheck.Of(null, $"bancos do cluster não lidos ({st})", "chaves de acesso desabilitadas em todos os bancos");
                    var dbs = r.Items("databases:items");
                    var on = dbs.Where(d => !string.Equals(AzureJson.Str(d, "properties.accessKeysAuthentication"), "Disabled", StringComparison.OrdinalIgnoreCase))
                        .Select(d => AzureJson.Str(d, "name") ?? "?").ToList();
                    return KnightItemCheck.Of(r.Has("databases:items") ? on.Count == 0 : null,
                        on.Count == 0 ? $"{dbs.Count} banco(s) com chaves de acesso desabilitadas" : $"banco(s) com chaves de acesso: {List(on)}",
                        "chaves de acesso desabilitadas em todos os bancos");
                },
                "{0} de {1} cluster(s) do Redis Enterprise aceitam autenticação por chave de acesso.",
                "Os {0} cluster(s) do Redis Enterprise não aceitam chave de acesso."),
            Ref(AzureRefs.Azd + "2.9#redis-enterprise"),
            // [AEGIS-KNIGHT-COVERAGE-04] Antes pesquisa pendente. Na Azure Managed Redis (Microsoft.Cache/redisEnterprise) o
            // Entra ID é o padrão e a versão estável 2025-07-01 não tem propriedade para ligá-lo ou desligá-lo: o que a
            // configuração decide é se as chaves de acesso continuam aceitas AO LADO dele.
            Ref(AzureRefs.Azd + "2.1#redis-enterprise", KnightReferenceMatch.Partial,
                "Na Azure Managed Redis a autenticação do Entra ID é o padrão e não tem chave liga/desliga na API estável; o critério verificável é não aceitar também as chaves de acesso. A atribuição de identidades às políticas de acesso dos bancos não é avaliada.")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-014", "Cache for Redis fora do canal de atualização estável", Cloud, SeverityLevel.High,
            "Manter os caches de produção no canal de atualização Stable.",
            "updateChannel = Stable.",
            Db("cache do Redis", AzureDb.Redis, (r, _) =>
                {
                    var ch = r.Str("properties.updateChannel");
                    return ch is null
                        ? KnightItemCheck.Of(true, "canal não definido — padrão documentado: Stable", "canal Stable")
                        : KnightItemCheck.Of(string.Equals(ch, "Stable", StringComparison.OrdinalIgnoreCase), $"canal: {ch}", "canal Stable");
                },
                "{0} de {1} cache(s) do Redis recebem atualizações antes do canal estável.",
                "Os {0} cache(s) do Redis estão no canal estável."),
            Ref(AzureRefs.Azd + "2.10")),

        // ======== Cosmos DB ==================================================================================
        Control(KnightService.AzureDatabases, "AK-AZ-DB-015", "Firewall do Cosmos DB aberto para todas as redes", Cloud, SeverityLevel.Medium,
            "Limitar o acesso às contas do Cosmos DB a redes virtuais e IPs selecionados (ou desabilitar o acesso público).",
            "Acesso público desabilitado, ou filtro de rede virtual / regras de IP definidos.",
            Db("conta do Cosmos DB", AzureDb.Cosmos, (r, _) =>
                {
                    var pub = r.Str("properties.publicNetworkAccess");
                    var vnet = r.Bool("properties.isVirtualNetworkFilterEnabled") == true;
                    var rules = r.Items("properties.ipRules").Select(i => AzureJson.Str(i, "ipAddressOrRange") ?? "").ToList();
                    // Uma regra 0.0.0.0/0 (ou ::/0) é "todas as redes" escrita como regra: não restringe nada.
                    var open = rules.Any(x => x is "0.0.0.0/0" or "::/0");
                    if (string.Equals(pub, "Disabled", StringComparison.OrdinalIgnoreCase)) return KnightItemCheck.Of(true, "acesso público desabilitado", "redes selecionadas");
                    return KnightItemCheck.Of((vnet || rules.Count > 0) && !open,
                        $"filtro de rede virtual: {YesNo(vnet)}; {rules.Count} regra(s) de IP{(open ? ", incluindo uma que libera toda a internet" : "")}", "redes selecionadas");
                },
                "{0} de {1} conta(s) do Cosmos DB aceitam conexões de todas as redes.",
                "As {0} conta(s) do Cosmos DB aceitam só redes selecionadas."),
            Ref(AzureRefs.Azd + "3.1")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-016", "Cosmos DB sem endpoint privado", Cloud, SeverityLevel.Medium,
            "Acessar as contas do Cosmos DB por endpoint privado.",
            "Ao menos uma conexão de endpoint privado aprovada.",
            Db("conta do Cosmos DB", AzureDb.Cosmos, (r, _) => PrivateEndpoint(r),
                "{0} de {1} conta(s) do Cosmos DB não têm endpoint privado aprovado.",
                "As {0} conta(s) do Cosmos DB têm endpoint privado aprovado."),
            Ref(AzureRefs.Azd + "3.2")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-017", "Cosmos DB aceita autenticação local por chave", Cloud, SeverityLevel.High,
            "Desabilitar a autenticação local (disableLocalAuth = true) e autorizar pelo Microsoft Entra ID.",
            "disableLocalAuth = true.",
            Db("conta do Cosmos DB", AzureDb.Cosmos, (r, _) => Flag(r, "properties.disableLocalAuth", true, "autenticação local", "desabilitada", "habilitada",
                    documentedDefault: false, defaultSource: "padrão: chaves da conta aceitas"),
                "{0} de {1} conta(s) do Cosmos DB aceitam chaves da conta.",
                "As {0} conta(s) do Cosmos DB exigem o Entra ID."),
            Ref(AzureRefs.Azd + "3.3")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-018", "Acesso público de rede habilitado no Cosmos DB", Cloud, SeverityLevel.High,
            "Desabilitar o acesso público de rede das contas do Cosmos DB.",
            "publicNetworkAccess = Disabled.",
            Db("conta do Cosmos DB", AzureDb.Cosmos, (r, _) => PublicAccessDisabled(r),
                "{0} de {1} conta(s) do Cosmos DB aceitam acesso pela rede pública.",
                "As {0} conta(s) do Cosmos DB têm o acesso público de rede desabilitado."),
            Ref(AzureRefs.Azd + "3.4")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-019", "Cosmos DB sem chave gerenciada pelo cliente", Cloud, SeverityLevel.Medium,
            "Cifrar as contas do Cosmos DB com dados críticos usando chave gerenciada pelo cliente.",
            "keyVaultKeyUri definido.",
            Db("conta do Cosmos DB", AzureDb.Cosmos, (r, _) =>
                {
                    var k = r.Has("properties.keyVaultKeyUri");
                    return KnightItemCheck.Of(k, k ? "chave do cliente configurada" : "chave gerenciada pela Microsoft", "chave gerenciada pelo cliente");
                },
                "{0} de {1} conta(s) do Cosmos DB usam chave gerenciada pela Microsoft.",
                "As {0} conta(s) do Cosmos DB usam chave gerenciada pelo cliente."),
            Ref(AzureRefs.Azd + "3.5", KnightReferenceMatch.Partial, "Avalia todas as contas; quais guardam dados críticos é decisão organizacional.")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-020", "Firewall do Cosmos DB com regra que libera todo o tráfego", Cloud, SeverityLevel.High,
            "Remover das regras de IP do Cosmos DB as faixas que liberam toda a internet (0.0.0.0/0) ou todos os datacenters do Azure (0.0.0.0).",
            "Nenhuma regra de IP 0.0.0.0/0, ::/0 ou 0.0.0.0.",
            Db("conta do Cosmos DB", AzureDb.Cosmos, (r, _) =>
                {
                    var wide = r.Items("properties.ipRules").Select(i => AzureJson.Str(i, "ipAddressOrRange") ?? "")
                        .Where(x => x is "0.0.0.0/0" or "0.0.0.0" or "::/0").ToList();
                    return KnightItemCheck.Of(wide.Count == 0, wide.Count == 0 ? "nenhuma regra de IP ampla" : $"regras amplas: {List(wide)}", "sem regras que liberem todo o tráfego");
                },
                "{0} de {1} conta(s) do Cosmos DB têm regra de IP que libera todo o tráfego.",
                "Nenhuma das {0} conta(s) do Cosmos DB libera todo o tráfego."),
            Ref(AzureRefs.Azd + "3.6")),

        // ======== Data Factory ===============================================================================
        Control(KnightService.AzureDatabases, "AK-AZ-DB-021", "Data Factory sem chave gerenciada pelo cliente", Cloud, SeverityLevel.Medium,
            "Cifrar as fábricas do Data Factory com chave gerenciada pelo cliente no Key Vault.",
            "encryption.vaultBaseUrl e keyName definidos.",
            Db("fábrica do Data Factory", v => v.OfType("Microsoft.DataFactory/factories"), (r, _) =>
                {
                    var k = r.Has("properties.encryption.vaultBaseUrl") && r.Has("properties.encryption.keyName");
                    return KnightItemCheck.Of(k, k ? "chave do cliente configurada" : "chave gerenciada pela Microsoft", "chave gerenciada pelo cliente");
                },
                "{0} de {1} fábrica(s) do Data Factory usam chave gerenciada pela Microsoft.",
                "As {0} fábrica(s) do Data Factory usam chave gerenciada pelo cliente."),
            Ref(AzureRefs.Azd + "4.1")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-022", "Data Factory sem identidade gerenciada", Cloud, SeverityLevel.Medium,
            "Atribuir identidade gerenciada às fábricas do Data Factory e usá-la nos serviços vinculados.",
            "identity.type diferente de None.",
            Db("fábrica do Data Factory", v => v.OfType("Microsoft.DataFactory/factories"), (r, _) => ManagedIdentity(r),
                "{0} de {1} fábrica(s) do Data Factory não têm identidade gerenciada.",
                "As {0} fábrica(s) do Data Factory têm identidade gerenciada."),
            Ref(AzureRefs.Azd + "4.2")),

        // ======== MySQL (servidor flexível) ==================================================================
        Control(KnightService.AzureDatabases, "AK-AZ-DB-023", "MySQL sem chave gerenciada pelo cliente", Cloud, SeverityLevel.Medium,
            "Cifrar os servidores MySQL com chave gerenciada pelo cliente.",
            "dataEncryption.type = AzureKeyVault.",
            Db("servidor MySQL", AzureDb.MySql, (r, _) => OneOf(r, "properties.dataEncryption.type", "criptografia de dados", "AzureKeyVault", "AzureKeyVault"),
                "{0} de {1} servidor(es) MySQL usam chave gerenciada pela Microsoft.",
                "Os {0} servidor(es) MySQL usam chave gerenciada pelo cliente."),
            Ref(AzureRefs.Azd + "5.1")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-024", "MySQL aceita autenticação além do Entra ID", Cloud, SeverityLevel.Medium,
            "Configurar um administrador do Entra ID e ligar aad_auth_only nos servidores MySQL.",
            "Parâmetro aad_auth_only = ON.",
            Db("servidor MySQL", AzureDb.MySql, (r, _) => AzureDb.ParamCheck(r, "aad_auth_only", AzureDb.On, "= ON"),
                "{0} de {1} servidor(es) MySQL aceitam usuários do próprio MySQL além do Entra ID.",
                "Os {0} servidor(es) MySQL aceitam somente o Entra ID."),
            Ref(AzureRefs.Azd + "5.2")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-025", "Acesso público de rede habilitado no MySQL", Cloud, SeverityLevel.High,
            "Desabilitar o acesso público de rede dos servidores MySQL.",
            "network.publicNetworkAccess = Disabled.",
            Db("servidor MySQL", AzureDb.MySql, (r, _) => PublicAccessDisabled(r, "properties.network.publicNetworkAccess"),
                "{0} de {1} servidor(es) MySQL aceitam acesso pela rede pública.",
                "Os {0} servidor(es) MySQL têm o acesso público de rede desabilitado."),
            Ref(AzureRefs.Azd + "5.3")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-026", "MySQL sem endpoint privado", Cloud, SeverityLevel.Medium,
            "Acessar os servidores MySQL por endpoint privado.",
            "Ao menos uma conexão de endpoint privado aprovada.",
            Db("servidor MySQL", AzureDb.MySql, (r, _) => PrivateEndpoint(r),
                "{0} de {1} servidor(es) MySQL não têm endpoint privado aprovado.",
                "Os {0} servidor(es) MySQL têm endpoint privado aprovado."),
            Ref(AzureRefs.Azd + "5.4")),

        AzureDbParam("AK-AZ-DB-027", "MySQL com log de auditoria desligado", "audit_log_enabled", AzureDb.MySql, "servidor MySQL", "servidor(es) MySQL", AzureDb.On, "= ON",
            SeverityLevel.High, "5.5", "o registro de auditoria do servidor"),
        AzureDbParam("AK-AZ-DB-028", "Auditoria do MySQL não registra conexões", "audit_log_events", AzureDb.MySql, "servidor MySQL", "servidor(es) MySQL",
            v => v.Split(',').Any(e => e.Trim().Equals("CONNECTION", StringComparison.OrdinalIgnoreCase)), "incluindo CONNECTION",
            SeverityLevel.Medium, "5.6", "os eventos de conexão na auditoria"),
        AzureDbParam("AK-AZ-DB-029", "MySQL sem arquivo de log de erros do servidor", "error_server_log_file", AzureDb.MySql, "servidor MySQL", "servidor(es) MySQL", AzureDb.On, "= ON",
            SeverityLevel.Medium, "5.7", "o arquivo de log de erros"),
        AzureDbParam("AK-AZ-DB-030", "MySQL aceita conexões sem transporte seguro", "require_secure_transport", AzureDb.MySql, "servidor MySQL", "servidor(es) MySQL", AzureDb.On, "= ON",
            SeverityLevel.High, "5.8", "o transporte seguro obrigatório"),
        AzureDbParam("AK-AZ-DB-031", "MySQL aceita TLS abaixo de 1.2", "tls_version", AzureDb.MySql, "servidor MySQL", "servidor(es) MySQL", AzureDb.Tls12Plus, "somente TLSv1.2 ou superior",
            SeverityLevel.High, "5.9", "a restrição a TLS 1.2 ou superior"),

        // ======== PostgreSQL (servidor flexível) =============================================================
        Control(KnightService.AzureDatabases, "AK-AZ-DB-032", "PostgreSQL sem chave gerenciada pelo cliente", Cloud, SeverityLevel.Medium,
            "Cifrar os servidores PostgreSQL com chave gerenciada pelo cliente.",
            "dataEncryption.type = AzureKeyVault.",
            Db("servidor PostgreSQL", AzureDb.Postgres, (r, _) => OneOf(r, "properties.dataEncryption.type", "criptografia de dados", "AzureKeyVault", "AzureKeyVault"),
                "{0} de {1} servidor(es) PostgreSQL usam chave gerenciada pela Microsoft.",
                "Os {0} servidor(es) PostgreSQL usam chave gerenciada pelo cliente."),
            Ref(AzureRefs.Azd + "6.1")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-033", "PostgreSQL aceita autenticação por senha", Cloud, SeverityLevel.Medium,
            "Habilitar a autenticação do Microsoft Entra ID e desabilitar a autenticação por senha nos servidores PostgreSQL.",
            "authConfig.activeDirectoryAuth = Enabled e passwordAuth = Disabled.",
            Db("servidor PostgreSQL", AzureDb.Postgres, (r, _) =>
                {
                    var ad = r.Get("properties.authConfig") is { } a ? AzureJson.Str(a, "activeDirectoryAuth") : null;
                    var pw = r.Get("properties.authConfig") is { } b ? AzureJson.Str(b, "passwordAuth") : null;
                    return KnightItemCheck.Of(ad is null || pw is null ? null
                            : string.Equals(ad, "Enabled", StringComparison.OrdinalIgnoreCase) && string.Equals(pw, "Disabled", StringComparison.OrdinalIgnoreCase),
                        $"Entra ID: {ad ?? "não informado"}; senha: {pw ?? "não informado"}", "somente Entra ID");
                },
                "{0} de {1} servidor(es) PostgreSQL aceitam autenticação por senha.",
                "Os {0} servidor(es) PostgreSQL aceitam somente o Entra ID."),
            Ref(AzureRefs.Azd + "6.2")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-034", "Acesso público de rede habilitado no PostgreSQL", Cloud, SeverityLevel.High,
            "Desabilitar o acesso público de rede dos servidores PostgreSQL.",
            "network.publicNetworkAccess = Disabled.",
            Db("servidor PostgreSQL", AzureDb.Postgres, (r, _) => PublicAccessDisabled(r, "properties.network.publicNetworkAccess"),
                "{0} de {1} servidor(es) PostgreSQL aceitam acesso pela rede pública.",
                "Os {0} servidor(es) PostgreSQL têm o acesso público de rede desabilitado."),
            Ref(AzureRefs.Azd + "6.3")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-035", "PostgreSQL sem endpoint privado", Cloud, SeverityLevel.Medium,
            "Acessar os servidores PostgreSQL por endpoint privado.",
            "Ao menos uma conexão de endpoint privado aprovada.",
            Db("servidor PostgreSQL", AzureDb.Postgres, (r, _) => PrivateEndpoint(r),
                "{0} de {1} servidor(es) PostgreSQL não têm endpoint privado aprovado.",
                "Os {0} servidor(es) PostgreSQL têm endpoint privado aprovado."),
            Ref(AzureRefs.Azd + "6.4")),

        AzureDbParam("AK-AZ-DB-036", "PostgreSQL sem limitação de conexões", "connection_throttle.enable", AzureDb.Postgres, "servidor PostgreSQL", "servidor(es) PostgreSQL", AzureDb.On, "= ON",
            SeverityLevel.Medium, "6.5", "a limitação de conexões"),
        AzureDbParam("AK-AZ-DB-037", "Logs do PostgreSQL retidos por até 3 dias", "logfiles.retention_days", AzureDb.Postgres, "servidor PostgreSQL", "servidor(es) PostgreSQL",
            v => int.TryParse(v, out var d) && d > 3, "> 3 dias", SeverityLevel.Medium, "6.6", "a retenção de logs acima de 3 dias"),
        AzureDbParam("AK-AZ-DB-038", "PostgreSQL não registra checkpoints", "log_checkpoints", AzureDb.Postgres, "servidor PostgreSQL", "servidor(es) PostgreSQL", AzureDb.On, "= ON",
            SeverityLevel.Medium, "6.7", "o registro de checkpoints"),
        AzureDbParam("AK-AZ-DB-039", "PostgreSQL não registra desconexões", "log_disconnections", AzureDb.Postgres, "servidor PostgreSQL", "servidor(es) PostgreSQL", AzureDb.On, "= ON",
            SeverityLevel.Medium, "6.8", "o registro de desconexões"),
        AzureDbParam("AK-AZ-DB-040", "PostgreSQL não registra conexões", "log_connections", AzureDb.Postgres, "servidor PostgreSQL", "servidor(es) PostgreSQL", AzureDb.On, "= ON",
            SeverityLevel.Medium, "6.9", "o registro de conexões"),
        AzureDbParam("AK-AZ-DB-041", "PostgreSQL aceita conexões sem transporte seguro", "require_secure_transport", AzureDb.Postgres, "servidor PostgreSQL", "servidor(es) PostgreSQL", AzureDb.On, "= ON",
            SeverityLevel.High, "6.10", "o transporte seguro obrigatório"),
        AzureDbParam("AK-AZ-DB-042", "PostgreSQL aceita TLS abaixo de 1.2", "ssl_min_protocol_version", AzureDb.Postgres, "servidor PostgreSQL", "servidor(es) PostgreSQL", AzureDb.Tls12Plus,
            "TLSv1.2 ou superior", SeverityLevel.High, "6.11", "a versão mínima TLS 1.2"),

        // ======== SQL do Azure ===============================================================================
        Control(KnightService.AzureDatabases, "AK-AZ-DB-043", "Servidores SQL sem auditoria", Cloud, SeverityLevel.High,
            "Habilitar a auditoria no nível do servidor SQL, com destino retido.",
            "auditingSettings do servidor com state = Enabled.",
            Db("servidor SQL", AzureDb.SqlServers, (r, _) => r.Str("audit:status") is { } st
                    ? KnightItemCheck.Of(null, $"auditoria não lida ({st})", "auditoria habilitada")
                    : OneOf(r, "audit:properties.state", "auditoria", "Enabled", "Enabled"),
                "{0} de {1} servidor(es) SQL estão sem auditoria.",
                "Os {0} servidor(es) SQL têm auditoria habilitada."),
            Ref(AzureRefs.Azd + "9.1")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-044", "Acesso público de rede habilitado no SQL", Cloud, SeverityLevel.High,
            "Desabilitar o acesso público de rede dos servidores SQL e usar endpoints privados.",
            "publicNetworkAccess = Disabled.",
            Db("servidor SQL", AzureDb.SqlServers, (r, _) => PublicAccessDisabled(r),
                "{0} de {1} servidor(es) SQL aceitam acesso pela rede pública.",
                "Os {0} servidor(es) SQL têm o acesso público de rede desabilitado."),
            Ref(AzureRefs.Azd + "9.2")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-045", "Firewall do SQL libera toda a internet", Cloud, SeverityLevel.High,
            "Remover as regras de firewall do SQL de 0.0.0.0 a 255.255.255.255 e liberar só os IPs necessários.",
            "Nenhuma regra de firewall 0.0.0.0–255.255.255.255.",
            Db("servidor SQL", AzureDb.SqlServers, (r, _) => AzureDb.FirewallRules(r, AzureDb.WholeInternet, "toda a internet"),
                "{0} de {1} servidor(es) SQL têm regra de firewall que libera toda a internet.",
                "Nenhum dos {0} servidor(es) SQL libera toda a internet."),
            Ref(AzureRefs.Azd + "9.3#internet-inteira")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-046", "Firewall do SQL libera todos os serviços do Azure", Cloud, SeverityLevel.Medium,
            "Desligar a opção “Permitir serviços e recursos do Azure” (regra 0.0.0.0) e usar endpoints privados ou regras de rede virtual.",
            "Nenhuma regra de firewall 0.0.0.0–0.0.0.0 (AllowAllWindowsAzureIps).",
            Db("servidor SQL", AzureDb.SqlServers, (r, _) => AzureDb.FirewallRules(r, AzureDb.AllAzure, "qualquer serviço do Azure (de qualquer cliente)"),
                "{0} de {1} servidor(es) SQL aceitam conexões de qualquer serviço do Azure.",
                "Nenhum dos {0} servidor(es) SQL aceita qualquer serviço do Azure."),
            Ref(AzureRefs.Azd + "9.3#servicos-do-azure")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-047", "Protetor do TDE do SQL sem chave do cliente", Cloud, SeverityLevel.Medium,
            "Configurar o protetor do TDE dos servidores SQL com chave gerenciada pelo cliente no Key Vault.",
            "encryptionProtector.serverKeyType = AzureKeyVault.",
            Db("servidor SQL", AzureDb.SqlServers, (r, _) => AzureDb.CmkProtector(r),
                "{0} de {1} servidor(es) SQL usam protetor do TDE gerenciado pelo serviço.",
                "Os {0} servidor(es) SQL usam protetor do TDE com chave do cliente."),
            Ref(AzureRefs.Azd + "9.4")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-048", "Protetor do TDE da instância gerenciada sem chave do cliente", Cloud, SeverityLevel.Medium,
            "Configurar o protetor do TDE das instâncias gerenciadas de SQL com chave gerenciada pelo cliente.",
            "encryptionProtector.serverKeyType = AzureKeyVault na instância.",
            Db("instância gerenciada de SQL", AzureDb.ManagedInstances, (r, _) => AzureDb.CmkProtector(r),
                "{0} de {1} instância(s) gerenciada(s) usam protetor do TDE gerenciado pelo serviço.",
                "As {0} instância(s) gerenciada(s) usam protetor do TDE com chave do cliente."),
            Ref(AzureRefs.Azd + "9.4#instancia-gerenciada")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-049", "Servidores SQL sem administrador do Entra ID", Cloud, SeverityLevel.Medium,
            "Configurar um administrador do Microsoft Entra ID nos servidores SQL (e preferir a autenticação somente pelo Entra ID).",
            "administrators.administratorType = ActiveDirectory.",
            Db("servidor SQL", AzureDb.SqlServers, (r, _) =>
                {
                    var t = r.Get("properties.administrators") is { } a ? AzureJson.Str(a, "administratorType") : null;
                    var only = r.Bool("aadOnly:properties.azureADOnlyAuthentication");
                    return KnightItemCheck.Of(string.Equals(t, "ActiveDirectory", StringComparison.OrdinalIgnoreCase),
                        $"administrador do Entra ID: {(t is null ? "não configurado" : t)}; somente Entra ID: {YesNo(only)}", "administrador do Entra ID configurado");
                },
                "{0} de {1} servidor(es) SQL não têm administrador do Entra ID.",
                "Os {0} servidor(es) SQL têm administrador do Entra ID."),
            Ref(AzureRefs.Azd + "9.5")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-050", "Bancos SQL sem criptografia transparente de dados", Cloud, SeverityLevel.High,
            "Habilitar a criptografia transparente de dados (TDE) em todos os bancos SQL.",
            "transparentDataEncryption.state = Enabled em cada banco (exceto master).",
            Db("banco SQL", v => v.OfType("Microsoft.Sql/servers/databases"), (r, _) => AzureDb.Tde(r),
                "{0} de {1} banco(s) SQL estão sem TDE.",
                "Os {0} banco(s) SQL têm TDE habilitado."),
            Ref(AzureRefs.Azd + "9.6")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-051", "Bancos da instância gerenciada sem criptografia transparente", Cloud, SeverityLevel.High,
            "Habilitar a criptografia transparente de dados nos bancos das instâncias gerenciadas.",
            "transparentDataEncryption.state = Enabled em cada banco da instância.",
            Db("banco de instância gerenciada", v => v.OfType("Microsoft.Sql/managedInstances/databases"), (r, _) => AzureDb.Tde(r),
                "{0} de {1} banco(s) de instância gerenciada estão sem TDE.",
                "Os {0} banco(s) de instância gerenciada têm TDE habilitado."),
            Ref(AzureRefs.Azd + "9.6#instancia-gerenciada")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-052", "Auditoria do SQL retida por menos de 90 dias", Cloud, SeverityLevel.Medium,
            "Reter os registros de auditoria do SQL por mais de 90 dias (ou 0, sem limite).",
            "auditingSettings.retentionDays = 0 ou > 90 nos servidores com auditoria.",
            Db("servidor SQL com auditoria", v => AzureDb.SqlServers(v).Where(s => string.Equals(s.Str("audit:properties.state"), "Enabled", StringComparison.OrdinalIgnoreCase)),
                (r, _) =>
                {
                    var d = r.Int("audit:properties.retentionDays");
                    return KnightItemCheck.Of(d is null ? null : d == 0 || d > 90, d is null ? "retenção não informada" : d == 0 ? "retenção sem limite (0)" : $"retenção de {d} dia(s)",
                        "retenção acima de 90 dias (ou 0)");
                },
                "{0} de {1} servidor(es) SQL retêm a auditoria por 90 dias ou menos.",
                "Os {0} servidor(es) SQL retêm a auditoria por mais de 90 dias."),
            Ref(AzureRefs.Azd + "9.7")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-053", "SQL aceita TLS abaixo de 1.2", Cloud, SeverityLevel.High,
            "Definir a versão mínima de TLS dos servidores SQL como 1.2.",
            "minimalTlsVersion = 1.2 ou 1.3.",
            Db("servidor SQL", AzureDb.SqlServers, (r, _) => OneOf(r, "properties.minimalTlsVersion", "TLS mínimo", "1.2 ou 1.3", "1.2", "1.3"),
                "{0} de {1} servidor(es) SQL aceitam TLS abaixo de 1.2.",
                "Os {0} servidor(es) SQL exigem TLS 1.2 ou superior."),
            Ref(AzureRefs.Azd + "9.8")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-054", "Instância gerenciada de SQL aceita TLS abaixo de 1.2", Cloud, SeverityLevel.High,
            "Definir a versão mínima de TLS das instâncias gerenciadas de SQL como 1.2.",
            "minimalTlsVersion = 1.2 ou 1.3 na instância.",
            Db("instância gerenciada de SQL", AzureDb.ManagedInstances, (r, _) => OneOf(r, "properties.minimalTlsVersion", "TLS mínimo", "1.2 ou 1.3", "1.2", "1.3"),
                "{0} de {1} instância(s) gerenciada(s) aceitam TLS abaixo de 1.2.",
                "As {0} instância(s) gerenciada(s) exigem TLS 1.2 ou superior."),
            Ref(AzureRefs.Azd + "9.8#instancia-gerenciada")),

        // [AEGIS-KNIGHT-COVERAGE-04] Antes pesquisa pendente: os serviços vinculados são lidos (versão estável 2018-06-01) e
        // o coletor grava só o RESUMO da origem de cada credencial — nenhum valor, cadeia de conexão ou nome de segredo.
        Control(KnightService.AzureDatabases, "AK-AZ-DB-055", "Credenciais do Data Factory fora do Key Vault", Cloud, SeverityLevel.Medium,
            "Guardar as credenciais dos serviços vinculados do Data Factory no Azure Key Vault (referência AzureKeyVaultSecret) ou usar identidade gerenciada.",
            "Nenhum serviço vinculado com segredo guardado na fábrica (SecureString), credencial criptografada (encryptedCredential) ou credencial em texto.",
            Db("fábrica do Data Factory", v => v.OfType("Microsoft.DataFactory/factories"), (r, _) =>
                {
                    const string expected = "credenciais dos serviços vinculados no Key Vault (ou sem credencial)";
                    if (r.Str("linked:status") is { } st) return KnightItemCheck.Of(null, $"serviços vinculados não lidos ({st})", expected);
                    if (!r.Has("linked:items")) return KnightItemCheck.Of(null, "serviços vinculados não informados", expected);
                    var items = r.Items("linked:items")
                        .Where(x => !string.Equals(AzureJson.Str(x, "type"), "AzureKeyVault", StringComparison.OrdinalIgnoreCase)).ToList();
                    var offenders = items.Where(x => (AzureJson.Int(x, "factorySecrets") ?? 0) + (AzureJson.Int(x, "encryptedCredentials") ?? 0)
                            + (AzureJson.Int(x, "plainCredentials") ?? 0) > 0)
                        .Select(x => $"{AzureJson.Str(x, "name") ?? "?"} ({AzureJson.Str(x, "type") ?? "?"})").ToList();
                    var kv = items.Sum(x => AzureJson.Int(x, "keyVaultRefs") ?? 0);
                    return KnightItemCheck.Of(offenders.Count == 0,
                        offenders.Count == 0
                            ? $"{items.Count} serviço(s) vinculado(s); {kv} credencial(is) por referência ao Key Vault"
                            : $"credencial fora do Key Vault em: {List(offenders, 3)}",
                        expected);
                },
                "{0} de {1} fábrica(s) do Data Factory guardam credencial fora do Key Vault.",
                "As {0} fábrica(s) do Data Factory não guardam credencial fora do Key Vault."),
            Ref(AzureRefs.Azd + "4.3")),

        // ======== Databricks =================================================================================
        Control(KnightService.AzureDatabricks, "AK-AZ-DBR-001", "Databricks fora de rede virtual do cliente", Cloud, SeverityLevel.Medium,
            "Implantar os workspaces do Databricks com injeção em rede virtual gerenciada pela organização.",
            "parameters.customVirtualNetworkId definido.",
            Dbr((r, _) =>
                {
                    var id = r.Get("properties.parameters.customVirtualNetworkId") is { } p ? AzureJson.Str(p, "value") : null;
                    return KnightItemCheck.Of(!string.IsNullOrEmpty(id), string.IsNullOrEmpty(id) ? "rede virtual gerenciada pelo Databricks" : $"rede virtual {id.Split('/').Last()}",
                        "rede virtual do cliente");
                },
                "{0} de {1} workspace(s) do Databricks não usam rede virtual da organização.",
                "Os {0} workspace(s) do Databricks usam rede virtual da organização."),
            Ref(AzureRefs.Az + "2.1.1")),

        Control(KnightService.AzureDatabricks, "AK-AZ-DBR-002", "Sub-redes do Databricks sem grupo de segurança de rede", Cloud, SeverityLevel.Medium,
            "Associar NSG às sub-redes pública e privada usadas pelos workspaces do Databricks.",
            "As duas sub-redes do workspace com NSG associado.",
            Dbr((r, v) =>
                {
                    var vnetId = r.Get("properties.parameters.customVirtualNetworkId") is { } p ? AzureJson.Str(p, "value") : null;
                    if (string.IsNullOrEmpty(vnetId)) return KnightItemCheck.Of(false, "workspace sem rede virtual do cliente (sub-redes fora do controle da organização)", "sub-redes com NSG");
                    var vnet = v.ById(vnetId);
                    if (vnet is null) return KnightItemCheck.Of(null, "rede virtual do workspace fora das assinaturas lidas", "sub-redes com NSG");
                    var names = new[] { "customPublicSubnetName", "customPrivateSubnetName" }
                        .Select(n => r.Get("properties.parameters." + n) is { } e ? AzureJson.Str(e, "value") : null).OfType<string>().ToList();
                    var missing = names.Where(n => !vnet.Items("subnets:items").Any(s =>
                        string.Equals(AzureJson.Str(s, "name"), n, StringComparison.OrdinalIgnoreCase)
                        && AzureJson.Str(s, "properties.networkSecurityGroup.id") is not null)).ToList();
                    return KnightItemCheck.Of(names.Count == 0 ? null : missing.Count == 0,
                        missing.Count == 0 ? $"sub-redes {List(names)} com NSG" : $"sub-rede(s) sem NSG: {List(missing)}", "sub-redes com NSG");
                },
                "{0} de {1} workspace(s) do Databricks têm sub-rede sem NSG.",
                "Os {0} workspace(s) do Databricks têm as sub-redes com NSG.",
                KnightCapability.AzureNetworking),
            Ref(AzureRefs.Az + "2.1.2")),

        Control(KnightService.AzureDatabricks, "AK-AZ-DBR-003", "Databricks sem chave gerenciada pelo cliente", Cloud, SeverityLevel.Medium,
            "Configurar chave gerenciada pelo cliente para os serviços gerenciados do workspace do Databricks (e para o DBFS quando houver dados críticos).",
            "encryption.entities.managedServices.keySource = Microsoft.Keyvault, ou criptografia do DBFS com chave do cliente.",
            Dbr((r, _) =>
                {
                    var ms = r.Get("properties.encryption") is { } e ? AzureJson.Str(e, "entities.managedServices.keySource") : null;
                    var dbfs = r.Get("properties.parameters.encryption") is { } p ? AzureJson.Str(p, "value.keySource") : null;
                    var ok = string.Equals(ms, "Microsoft.Keyvault", StringComparison.OrdinalIgnoreCase) || string.Equals(dbfs, "Microsoft.Keyvault", StringComparison.OrdinalIgnoreCase);
                    return KnightItemCheck.Of(ok, $"serviços gerenciados: {ms ?? "chave da Microsoft"}; DBFS: {dbfs ?? "chave da Microsoft"}", "chave gerenciada pelo cliente");
                },
                "{0} de {1} workspace(s) do Databricks usam só chaves gerenciadas pela Microsoft.",
                "Os {0} workspace(s) do Databricks usam chave gerenciada pelo cliente."),
            Ref(AzureRefs.Az + "2.1.8", KnightReferenceMatch.Partial, "Aceita a chave do cliente nos serviços gerenciados ou no DBFS; quais dados são críticos é decisão organizacional.")),

        Control(KnightService.AzureDatabricks, "AK-AZ-DBR-004", "Clusters do Databricks com IP público", Cloud, SeverityLevel.Medium,
            "Habilitar a conectividade segura de cluster (sem IP público) nos workspaces do Databricks.",
            "parameters.enableNoPublicIp = true.",
            Dbr((r, _) =>
                {
                    var on = r.Get("properties.parameters.enableNoPublicIp") is { } p ? AzureJson.Bool(p, "value") : null;
                    return KnightItemCheck.Of(on, $"sem IP público: {YesNo(on)}", "sem IP público nos clusters");
                },
                "{0} de {1} workspace(s) do Databricks dão IP público aos nós dos clusters.",
                "Os {0} workspace(s) do Databricks usam clusters sem IP público."),
            Ref(AzureRefs.Az + "2.1.9")),

        Control(KnightService.AzureDatabricks, "AK-AZ-DBR-005", "Acesso público de rede habilitado no Databricks", Cloud, SeverityLevel.Medium,
            "Desabilitar o acesso público de rede aos workspaces do Databricks (Private Link de front-end).",
            "publicNetworkAccess = Disabled.",
            Dbr((r, _) => PublicAccessDisabled(r),
                "{0} de {1} workspace(s) do Databricks aceitam acesso pela rede pública.",
                "Os {0} workspace(s) do Databricks têm o acesso público de rede desabilitado."),
            Ref(AzureRefs.Az + "2.1.10")),

        Control(KnightService.AzureDatabricks, "AK-AZ-DBR-006", "Databricks sem endpoint privado", Cloud, SeverityLevel.Medium,
            "Acessar os workspaces do Databricks por endpoint privado.",
            "Ao menos uma conexão de endpoint privado aprovada.",
            Dbr((r, _) => PrivateEndpoint(r),
                "{0} de {1} workspace(s) do Databricks não têm endpoint privado aprovado.",
                "Os {0} workspace(s) do Databricks têm endpoint privado aprovado."),
            Ref(AzureRefs.Az + "2.1.11")),
    };

    private static KnightIndicatorDefinition AzureDbParam(string id, string title, string param, Func<AzureView, IEnumerable<AzureResource>> pop, string noun,
        string plural, Func<string, bool> ok, string expected, SeverityLevel sev, string refKey, string what) =>
        Control(KnightService.AzureDatabases, id, title, KnightIndicatorCategory.CloudInfrastructure, sev,
            $"Definir o parâmetro de servidor {param} {expected} para ligar {what}.",
            $"Parâmetro de servidor {param} {expected}.",
            Db(noun, pop, (r, _) => AzureDb.ParamCheck(r, param, ok, expected),
                "{0} de {1} " + plural + " estão sem " + what + ".",
                "Os {0} " + plural + " têm " + what + "."),
            Ref(AzureRefs.Azd + refKey));
}

/// <summary>Leituras comuns de computação.</summary>
internal static class AzureCmp
{
    public static KnightItemCheck Cmk(AzureResource disk)
    {
        var t = disk.Str("properties.encryption.type");
        return KnightItemCheck.Of(t is null ? null : t is "EncryptionAtRestWithCustomerKey" or "EncryptionAtRestWithPlatformAndCustomerKeys",
            $"criptografia: {t ?? "não informada"}", "chave gerenciada pelo cliente");
    }

    private static readonly (string Publisher, string Type)[] Known =
    {
        ("Microsoft.Azure.AzureDefenderForServers", "MDE.Windows"),
        ("Microsoft.Azure.AzureDefenderForServers", "MDE.Linux"),
        ("Microsoft.Azure.Security", "IaaSAntimalware"),
    };

    public static KnightItemCheck EndpointProtection(AzureResource vm)
    {
        if (vm.Str("ext:status") is { } st) return KnightItemCheck.Of(null, $"extensões não lidas ({st})", "extensão de proteção de endpoint");
        var found = vm.Items("ext:items").Where(e => Known.Any(k =>
                string.Equals(AzureJson.Str(e, "properties.publisher"), k.Publisher, StringComparison.OrdinalIgnoreCase)
                && string.Equals(AzureJson.Str(e, "properties.type"), k.Type, StringComparison.OrdinalIgnoreCase)))
            .Select(e => $"{AzureJson.Str(e, "properties.type")} ({AzureJson.Str(e, "properties.provisioningState") ?? "?"})").ToList();
        return found.Count > 0
            ? KnightItemCheck.Of(true, $"extensão(ões): {List(found)}", "extensão de proteção de endpoint")
            : KnightItemCheck.Of(null, "nenhuma extensão de proteção de endpoint reconhecida (uma solução sem extensão não é visível pelo Resource Manager)",
                "extensão de proteção de endpoint");
    }
}
