using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Application.Knight.Reference;
using AegisScore.Domain;
using static AegisScore.Application.Knight.Catalog.AzureRuleKit;
using static AegisScore.Application.Knight.Catalog.KnightRuleKit;

namespace AegisScore.Application.Knight.Catalog;

// ============================================================================
//  [AEGIS-KNIGHT-CLOSURE-01] Controles do Azure sobre as leituras complementares
// ============================================================================
// • Configurações de diagnóstico (assinatura e recursos) e contatos de segurança do Defender para Nuvem, lidos em versão
//   PREVIEW: o controle depende da capacidade própria da leitura; indisponibilidade ou resposta fora do contrato deixa o
//   item desconhecido e o controle não avaliado — nunca aprovado. A versão preview aparece nas informações técnicas.
// • Referências ao Key Vault do App Service: avaliação PARCIAL — o que a referência mostra (resolução) é avaliado; se as
//   demais configurações guardam segredo em texto não é visível sem a leitura que devolve os valores, que não é usada.
// • API do workspace do Databricks: só quando habilitada em Integrações; sem ela os controles ficam não avaliados com o
//   motivo, e um workspace sem a aplicação adicionada fica desconhecido.

/// <summary>Tipos de recurso cujos logs de recurso o KNIGHT examina, e o serviço que emite os logs.</summary>
public static class AzureResourceLogs
{
    /// <summary>Tipo → sufixo do serviço com os logs (o armazenamento emite pelo serviço de blob, não pela conta).</summary>
    public static IReadOnlyDictionary<string, string> Types { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.KeyVault/vaults"] = "",
        ["Microsoft.DocumentDB/databaseAccounts"] = "",
        ["Microsoft.Batch/batchAccounts"] = "",
        ["Microsoft.Databricks/workspaces"] = "",
        ["Microsoft.Web/sites"] = "",
        ["Microsoft.Network/networkSecurityGroups"] = "",
        ["Microsoft.Network/applicationGateways"] = "",
        ["Microsoft.Network/publicIPAddresses"] = "",
        ["Microsoft.Sql/servers/databases"] = "",
        ["Microsoft.DBforPostgreSQL/flexibleServers"] = "",
        ["Microsoft.DBforMySQL/flexibleServers"] = "",
        ["Microsoft.Cache/Redis"] = "",
        ["Microsoft.DataFactory/factories"] = "",
        ["Microsoft.Storage/storageAccounts"] = "/blobServices/default",
    };

    /// <summary>Famílias estáveis que produzem a população dos tipos acima.</summary>
    public static readonly KnightCapability[] PopulationFamilies =
    {
        KnightCapability.AzureKeyVault, KnightCapability.AzureDatabases, KnightCapability.AzureCompute, KnightCapability.AzureDatabricks,
        KnightCapability.AzureAppService, KnightCapability.AzureNetworking, KnightCapability.AzureStorage,
    };

    public const string SubscriptionDiagnosticType = "Microsoft.Insights/diagnosticSettings";
    public const string SecurityContactType = "Microsoft.Security/securityContacts";

    public static bool HasDestination(JsonElement setting) =>
        AzureJson.Str(setting, "properties.workspaceId") is not null || AzureJson.Str(setting, "properties.storageAccountId") is not null
        || AzureJson.Str(setting, "properties.eventHubAuthorizationRuleId") is not null || AzureJson.Str(setting, "properties.marketplacePartnerId") is not null;

    private static string Destinations(JsonElement s) => List(new[]
    {
        AzureJson.Str(s, "properties.workspaceId") is null ? null : "Log Analytics",
        AzureJson.Str(s, "properties.storageAccountId") is null ? null : "armazenamento",
        AzureJson.Str(s, "properties.eventHubAuthorizationRuleId") is null ? null : "hub de eventos",
        AzureJson.Str(s, "properties.marketplacePartnerId") is null ? null : "parceiro",
    }.OfType<string>());

    /// <summary>Categorias (ou grupos de categorias) HABILITADAS de uma configuração.</summary>
    public static IEnumerable<string> EnabledLogs(JsonElement setting) =>
        AzureJson.Items(setting, "properties.logs")
            .Where(l => AzureJson.Bool(l, "enabled") == true)
            .Select(l => AzureJson.Str(l, "category") ?? (AzureJson.Str(l, "categoryGroup") is { } g ? "grupo " + g : null))
            .OfType<string>();

    private static string StatusLabel(string status) => status switch
    {
        "NotFound" => "o recurso não respondeu pela operação (404)",
        "NotRead" => "não lidas: a leitura foi interrompida nesta assinatura ou atingiu o teto da coleta",
        "ContractChanged" => "resposta fora do contrato documentado da versão preview",
        "InsufficientPermission" => "autorização recusada",
        _ => status,
    };

    /// <summary>
    /// Logs de recurso do item: alguma configuração com destino tem habilitada uma categoria aceita por
    /// <paramref name="wanted"/>. Leitura falha ou ausente = desconhecido.
    /// </summary>
    public static KnightItemCheck Logs(AzureResource r, Func<JsonElement, bool> wanted, string expected)
    {
        if (r.Str("diag:status") is { } st)
            return KnightItemCheck.Of(null, $"configurações de diagnóstico não lidas ({StatusLabel(st)})", expected);
        if (!r.Has("diag:items"))
            return KnightItemCheck.Of(null, "configurações de diagnóstico não lidas nesta coleta", expected);
        var settings = r.Items("diag:items");
        if (settings.Count == 0) return KnightItemCheck.Of(false, "nenhuma configuração de diagnóstico", expected);
        var ok = settings.Any(s => HasDestination(s) && AzureJson.Items(s, "properties.logs").Any(l => AzureJson.Bool(l, "enabled") == true && wanted(l)));
        var found = string.Join("; ", settings.Take(3).Select(s =>
            $"{AzureJson.Str(s, "name") ?? "configuração"}: destino {(HasDestination(s) ? Destinations(s) : "nenhum")}, logs {List(EnabledLogs(s), 6)}"));
        return KnightItemCheck.Of(ok, Trim(found, 900), expected);
    }

    /// <summary>Qualquer categoria ou grupo de categorias de log habilitado.</summary>
    public static bool AnyLog(JsonElement l) => AzureJson.Str(l, "category") is not null || AzureJson.Str(l, "categoryGroup") is not null;

    /// <summary>Categoria específica, ou um grupo que a inclui (audit/allLogs).</summary>
    public static Func<JsonElement, bool> CategoryOrGroup(string category, params string[] groups) => l =>
        string.Equals(AzureJson.Str(l, "category"), category, StringComparison.OrdinalIgnoreCase)
        || groups.Any(g => string.Equals(AzureJson.Str(l, "categoryGroup"), g, StringComparison.OrdinalIgnoreCase));
}

public static class AzureClosureControls
{
    private const KnightIndicatorCategory Cloud = KnightIndicatorCategory.CloudInfrastructure;

    private static readonly string[] ActivityCategories = { "Administrative", "Alert", "Policy", "Security" };

    /// <summary>Configurações de diagnóstico da ASSINATURA (exportação do log de atividades).</summary>
    private static IReadOnlyList<AzureResource> SubDiag(AzureView v, string sub) =>
        v.InSubscription(AzureResourceLogs.SubscriptionDiagnosticType, sub).Where(r => r.ParentId is not null).ToList();

    private static IReadOnlyList<AzureResource> Contacts(AzureView v, string sub) =>
        v.InSubscription(AzureResourceLogs.SecurityContactType, sub).ToList();

    private static AzureCheck PerSub(KnightCapability family, Func<AzureResource, AzureView, KnightItemCheck> check, string exposed, string passed) =>
        new(family, "assinatura", v => v.SubscriptionItems, check, exposed, passed);

    private static AzureCheck Diag(KnightCapability populationFamily, string noun, string type, Func<JsonElement, bool> wanted, string expected,
        string exposed, string passed) =>
        new(populationFamily, noun, v => v.OfType(type), (r, _) => AzureResourceLogs.Logs(r, wanted, expected), exposed, passed,
            new[] { KnightCapability.AzureResourceDiagnostics });

    /// <summary>Contato de segurança da assinatura que satisfaz <paramref name="ok"/> (contato desabilitado não conta).</summary>
    private static KnightItemCheck Contact(AzureResource sub, AzureView v, Func<AzureResource, bool> ok, Func<AzureResource, string> describe, string expected)
    {
        var contacts = Contacts(v, sub.SubscriptionId);
        if (contacts.Count == 0) return KnightItemCheck.Of(false, "nenhum contato de segurança configurado", expected);
        var enabled = contacts.Where(c => c.Bool("properties.isEnabled") != false).ToList();
        if (enabled.Count == 0) return KnightItemCheck.Of(false, "contato de segurança desabilitado", expected);
        return KnightItemCheck.Of(enabled.Any(ok), Trim(string.Join("; ", enabled.Select(describe)), 900), expected);
    }

    private static IEnumerable<JsonElement> Sources(AzureResource c, string type) =>
        c.Items("properties.notificationsSources").Where(s => string.Equals(AzureJson.Str(s, "sourceType"), type, StringComparison.OrdinalIgnoreCase));

    // ---- Databricks: API do workspace -------------------------------------------------------------------------------

    /// <summary>Resumo de uma leitura da API do workspace, ou o motivo de não haver.</summary>
    private static (JsonElement? Value, string? Missing) Dbx(AzureResource w, string key)
    {
        if (w.Str("dbx:status") is { } ws) return (null, ws == "NoWorkspaceUrl" ? "endereço do workspace não informado pelo Resource Manager" : ws);
        if (w.Str($"dbx:{key}:status") is { } st)
            return (null, st switch
            {
                "InsufficientPermission" => "autorização recusada pelo workspace (a aplicação não foi adicionada como administradora)",
                "AuthFailure" => "autenticação recusada pelo workspace (a aplicação não foi adicionada ao workspace)",
                "NotFound" => "operação não encontrada no workspace (404)",
                "ContractChanged" => "resposta fora do contrato documentado",
                _ => st,
            });
        return w.Get("dbx:" + key) is { } v ? (v, null) : (null, "API do workspace não lida nesta coleta");
    }

    /// <summary>
    /// Avaliação pela API do workspace: população = workspaces (família estável), aprovação exige também a leitura da API
    /// concluída. Workspace sem itens a avaliar (<paramref name="skip"/>) sai da população.
    /// </summary>
    private static KnightControlOutcome DbxEvaluate(AzureView v, Func<AzureResource, KnightItemCheck> check, string exposed, string passed,
        Func<AzureResource, bool>? skip = null, string? emptyText = null)
    {
        var all = v.OfType("Microsoft.Databricks/workspaces").ToList();
        var (complete, reason) = v.Completeness(KnightCapability.AzureDatabricks);
        if (all.Count == 0) return EmptyOrIncomplete(v, KnightCapability.AzureDatabricks, "workspace do Databricks");
        var (apiOk, apiReason) = v.Completeness(KnightCapability.AzureDatabricksWorkspaceApi);
        var items = skip is null ? all : all.Where(w => !skip(w)).ToList();
        if (items.Count == 0 && complete && apiOk)
            return KnightControlOutcome.NotApplicable(emptyText ?? "nenhum item a avaliar nos workspaces do escopo.");
        return Population(items, check, Describe,
            (n, t) => string.Format(CultureInfo.InvariantCulture, exposed, n, t),
            t => string.Format(CultureInfo.InvariantCulture, passed, t),
            () => KnightControlOutcome.NotEvaluated(Trim(string.Join(" ", new[] { reason, apiReason }.OfType<string>()))),
            complete && apiOk, string.Join(" ", new[] { reason, apiReason }.OfType<string>()));
    }

    public static IReadOnlyList<KnightIndicatorDefinition> Definitions { get; } = new[]
    {
        // ======== Azure Monitor: exportação do log de atividades (versão preview) =====================================
        Control(KnightService.AzureMonitor, "AK-AZ-MON-017", "Log de atividades da assinatura não exportado", Cloud, SeverityLevel.Medium,
            "Criar, em cada assinatura, uma configuração de diagnóstico que exporte o log de atividades para um destino retido (Log Analytics, armazenamento ou hub de eventos).",
            "Ao menos uma configuração de diagnóstico da assinatura com destino.",
            PerSub(KnightCapability.AzureActivityLogExport, (s, v) =>
                {
                    var settings = SubDiag(v, s.SubscriptionId);
                    var with = settings.Count(x => x.Has("properties.workspaceId") || x.Has("properties.storageAccountId")
                                                   || x.Has("properties.eventHubAuthorizationRuleId") || x.Has("properties.marketplacePartnerId"));
                    return KnightItemCheck.Of(with > 0, settings.Count == 0 ? "nenhuma configuração de diagnóstico da assinatura"
                        : $"{settings.Count} configuração(ões), {with} com destino", "configuração de diagnóstico da assinatura com destino");
                },
                "{0} de {1} assinatura(s) não exportam o log de atividades.",
                "As {0} assinatura(s) exportam o log de atividades para um destino."),
            Ref(AzureRefs.Az + "6.1.1.1")),

        Control(KnightService.AzureMonitor, "AK-AZ-MON-018", "Exportação do log de atividades sem as categorias exigidas", Cloud, SeverityLevel.Medium,
            "Habilitar, na configuração de diagnóstico da assinatura, as categorias Administrative, Alert, Policy e Security com destino.",
            "Categorias Administrative, Alert, Policy e Security habilitadas numa configuração da assinatura com destino.",
            PerSub(KnightCapability.AzureActivityLogExport, (s, v) =>
                {
                    var enabled = SubDiag(v, s.SubscriptionId)
                        .Where(x => x.Has("properties.workspaceId") || x.Has("properties.storageAccountId")
                                    || x.Has("properties.eventHubAuthorizationRuleId") || x.Has("properties.marketplacePartnerId"))
                        .SelectMany(x => x.Items("properties.logs"))
                        .Where(l => AzureJson.Bool(l, "enabled") == true)
                        .Select(l => AzureJson.Str(l, "category") ?? "")
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var missing = ActivityCategories.Where(cat => !enabled.Contains(cat)).ToList();
                    return KnightItemCheck.Of(missing.Count == 0,
                        missing.Count == 0 ? "categorias exigidas habilitadas com destino" : $"sem destino para: {List(missing, 4)}",
                        "Administrative, Alert, Policy e Security habilitadas com destino");
                },
                "{0} de {1} assinatura(s) não exportam todas as categorias exigidas do log de atividades.",
                "As {0} assinatura(s) exportam as categorias exigidas do log de atividades."),
            Ref(AzureRefs.Az + "6.1.1.2")),

        Custom(KnightService.AzureMonitor, "AK-AZ-MON-019", "Conta de armazenamento do log de atividades sem chave do cliente", Cloud, SeverityLevel.Medium,
            "Cifrar com chave gerenciada pelo cliente (Key Vault) a conta de armazenamento que recebe o log de atividades.",
            "Conta de armazenamento de destino com criptografia por chave do cliente (keySource = Microsoft.Keyvault).",
            (c, v) =>
            {
                var (complete, reason) = v.Completeness(KnightCapability.AzureActivityLogExport);
                var (storageOk, storageReason) = v.Completeness(KnightCapability.AzureStorage);
                var settings = v.OfType(AzureResourceLogs.SubscriptionDiagnosticType)
                    .Where(x => x.ParentId is not null && x.Str("properties.storageAccountId") is not null).ToList();
                return Population(settings, s =>
                    {
                        var target = s.Str("properties.storageAccountId")!;
                        var account = v.ById(target);
                        const string expected = "conta de destino cifrada com chave do cliente";
                        if (account is null) return KnightItemCheck.Of(null, $"conta de destino {target.Split('/').Last()} fora das assinaturas lidas", expected);
                        var source = account.Str("properties.encryption.keySource");
                        return KnightItemCheck.Of(source is null ? null : string.Equals(source, "Microsoft.Keyvault", StringComparison.OrdinalIgnoreCase),
                            $"conta {account.Name}: chave {(source is null ? "não informada" : string.Equals(source, "Microsoft.Keyvault", StringComparison.OrdinalIgnoreCase) ? "do cliente (Key Vault)" : "gerenciada pela Microsoft")}",
                            expected);
                    }, Describe,
                    (n, t) => $"{n} de {t} exportação(ões) do log de atividades vão para conta sem chave do cliente.",
                    t => $"As {t} exportação(ões) do log de atividades para armazenamento usam conta cifrada com chave do cliente.",
                    () => complete
                        ? KnightControlOutcome.NotApplicable($"nenhuma exportação do log de atividades para conta de armazenamento nas {v.Scope.Count} assinatura(s) do escopo.")
                        : KnightControlOutcome.NotEvaluated(Trim(reason ?? "leitura incompleta.")),
                    complete && storageOk, string.Join(" ", new[] { reason, storageReason }.OfType<string>()));
            },
            Ref(AzureRefs.Az + "6.1.1.3")),

        // ======== Logs de recurso (versão preview) ============================================================
        Control(KnightService.AzureKeyVault, "AK-AZ-KV-011", "Cofres de chaves sem logs de auditoria exportados", Cloud, SeverityLevel.Medium,
            "Criar, em cada cofre, uma configuração de diagnóstico que envie a categoria AuditEvent (ou o grupo audit/allLogs) a um destino retido.",
            "Configuração de diagnóstico do cofre com AuditEvent (ou grupo audit/allLogs) habilitado e destino.",
            Diag(KnightCapability.AzureKeyVault, "cofre de chaves", "Microsoft.KeyVault/vaults", AzureResourceLogs.CategoryOrGroup("AuditEvent", "audit", "allLogs"),
                "AuditEvent habilitado com destino",
                "{0} de {1} cofre(s) não exportam os logs de auditoria.",
                "Os {0} cofre(s) exportam os logs de auditoria."),
            Ref(AzureRefs.Az + "6.1.1.4")),

        Control(KnightService.AzureDatabases, "AK-AZ-DB-056", "Contas do Cosmos DB sem logs de diagnóstico", Cloud, SeverityLevel.Medium,
            "Criar, em cada conta do Cosmos DB, uma configuração de diagnóstico com categorias de log habilitadas e destino retido.",
            "Configuração de diagnóstico com ao menos uma categoria de log habilitada e destino.",
            Diag(KnightCapability.AzureDatabases, "conta do Cosmos DB", "Microsoft.DocumentDB/databaseAccounts", AzureResourceLogs.AnyLog,
                "categoria de log habilitada com destino",
                "{0} de {1} conta(s) do Cosmos DB não exportam logs de diagnóstico.",
                "As {0} conta(s) do Cosmos DB exportam logs de diagnóstico."),
            Ref(AzureRefs.Azd + "3.7")),

        Control(KnightService.AzureCompute, "AK-AZ-CMP-018", "Contas do Batch sem logs de diagnóstico", Cloud, SeverityLevel.Medium,
            "Criar, em cada conta do Batch, uma configuração de diagnóstico com categorias de log habilitadas e destino retido.",
            "Configuração de diagnóstico com ao menos uma categoria de log habilitada e destino.",
            Diag(KnightCapability.AzureCompute, "conta do Batch", "Microsoft.Batch/batchAccounts", AzureResourceLogs.AnyLog,
                "categoria de log habilitada com destino",
                "{0} de {1} conta(s) do Batch não exportam logs de diagnóstico.",
                "As {0} conta(s) do Batch exportam logs de diagnóstico."),
            Ref(AzureRefs.Azc + "15.7")),

        Control(KnightService.AzureDatabricks, "AK-AZ-DBR-007", "Workspaces do Databricks sem entrega de logs de diagnóstico", Cloud, SeverityLevel.Medium,
            "Criar, em cada workspace do Databricks (camada Premium), uma configuração de diagnóstico com as categorias de log habilitadas e destino retido.",
            "Configuração de diagnóstico do workspace com ao menos uma categoria de log habilitada e destino.",
            Diag(KnightCapability.AzureDatabricks, "workspace do Databricks", "Microsoft.Databricks/workspaces", AzureResourceLogs.AnyLog,
                "categoria de log habilitada com destino",
                "{0} de {1} workspace(s) do Databricks não entregam logs de diagnóstico.",
                "Os {0} workspace(s) do Databricks entregam logs de diagnóstico."),
            Ref(AzureRefs.Az + "2.1.7")),

        Custom(KnightService.AzureMonitor, "AK-AZ-MON-020", "Recursos sem logs de recurso exportados", Cloud, SeverityLevel.Medium,
            "Criar configurações de diagnóstico que exportem os logs de recurso de cada serviço que os emite (de preferência por política do Azure).",
            "Cada recurso dos tipos examinados com uma configuração de diagnóstico com categoria de log habilitada e destino.",
            (c, v) =>
            {
                var items = v.Resources.Where(r => AzureResourceLogs.Types.ContainsKey(r.Type)).ToList();
                var parts = new List<string>();
                var complete = true;
                foreach (var f in AzureResourceLogs.PopulationFamilies.Append(KnightCapability.AzureResourceDiagnostics))
                {
                    var (ok, why) = v.Completeness(f);
                    if (!ok) { complete = false; if (why is not null) parts.Add(why); }
                }
                return Population(items, r => AzureResourceLogs.Logs(r, AzureResourceLogs.AnyLog, "categoria de log habilitada com destino"), Describe,
                    (n, t) => $"{n} de {t} recurso(s) dos tipos examinados não exportam logs de recurso.",
                    t => $"Os {t} recurso(s) dos tipos examinados exportam logs de recurso.",
                    () => complete
                        ? KnightControlOutcome.NotApplicable($"nenhum recurso dos tipos examinados nas {v.Scope.Count} assinatura(s) do escopo.")
                        : KnightControlOutcome.NotEvaluated(Trim(string.Join(" ", parts))),
                    complete, parts.Count == 0 ? null : Trim(string.Join(" ", parts), 900));
            },
            Ref(AzureRefs.Az + "6.1.4", KnightReferenceMatch.Partial,
                "Examina os tipos de recurso que o KNIGHT lê e que emitem logs de recurso (cofres, Cosmos DB, Batch, Databricks, App Service, "
                + "NSG, gateways de aplicativo, IPs públicos, bancos SQL, PostgreSQL, MySQL, Redis, Data Factory e o serviço de blob das contas "
                + "de armazenamento); recursos de outros tipos não são lidos pela coleta e não entram.")),

        // ======== Defender para Nuvem: contatos de segurança (versão preview) =================================
        Control(KnightService.DefenderForCloud, "AK-AZ-MDC-021", "Proprietários da assinatura não notificados sobre alertas", Cloud, SeverityLevel.Medium,
            "Habilitar, nas notificações por e-mail do Defender para Nuvem, o envio aos proprietários da assinatura.",
            "Contato de segurança habilitado com notificação por papel ligada (state On) incluindo o papel Owner.",
            PerSub(KnightCapability.AzureSecurityContacts, (s, v) => Contact(s, v,
                    c => string.Equals(c.Str("properties.notificationsByRole.state"), "On", StringComparison.OrdinalIgnoreCase)
                         && c.Strings("properties.notificationsByRole.roles").Contains("Owner", StringComparer.OrdinalIgnoreCase),
                    c => $"notificação por papel: {c.Str("properties.notificationsByRole.state") ?? "não informada"} ({List(c.Strings("properties.notificationsByRole.roles"), 4)})",
                    "notificação por papel ligada para Owner"),
                "{0} de {1} assinatura(s) não notificam os proprietários sobre alertas do Defender.",
                "As {0} assinatura(s) notificam os proprietários sobre alertas do Defender."),
            Ref(AzureRefs.Az + "8.1.12")),

        Control(KnightService.DefenderForCloud, "AK-AZ-MDC-022", "Sem contato de segurança adicional por e-mail", Cloud, SeverityLevel.Medium,
            "Informar, nas notificações por e-mail do Defender para Nuvem, ao menos um endereço de contato de segurança (de preferência uma caixa da equipe).",
            "Contato de segurança habilitado com ao menos um endereço de e-mail.",
            PerSub(KnightCapability.AzureSecurityContacts, (s, v) => Contact(s, v,
                    c => (c.Int("emails:count") ?? 0) > 0,
                    c => $"{c.Int("emails:count") ?? 0} endereço(s) de e-mail",
                    "ao menos um endereço de contato de segurança"),
                "{0} de {1} assinatura(s) não têm endereço de contato de segurança.",
                "As {0} assinatura(s) têm endereço de contato de segurança."),
            Ref(AzureRefs.Az + "8.1.13")),

        Control(KnightService.DefenderForCloud, "AK-AZ-MDC-023", "Notificação de alertas por severidade desligada", Cloud, SeverityLevel.Medium,
            "Habilitar a notificação por e-mail de alertas do Defender para Nuvem a partir da severidade alta (ou menor).",
            "Origem de notificação Alert com severidade mínima High, Medium ou Low.",
            PerSub(KnightCapability.AzureSecurityContacts, (s, v) => Contact(s, v,
                    c => Sources(c, "Alert").Any(x => AzureJson.Str(x, "minimalSeverity") is "High" or "Medium" or "Low"),
                    c => Sources(c, "Alert").Select(x => AzureJson.Str(x, "minimalSeverity")).FirstOrDefault() is { } sev
                        ? $"alertas a partir da severidade {sev}" : "sem notificação de alertas",
                    "notificação de alertas a partir da severidade alta"),
                "{0} de {1} assinatura(s) não notificam alertas do Defender por severidade.",
                "As {0} assinatura(s) notificam alertas do Defender por severidade."),
            Ref(AzureRefs.Az + "8.1.14")),

        Control(KnightService.DefenderForCloud, "AK-AZ-MDC-024", "Notificação de caminhos de ataque desligada", Cloud, SeverityLevel.Low,
            "Habilitar a notificação por e-mail de caminhos de ataque do Defender para Nuvem a partir de um nível de risco.",
            "Origem de notificação AttackPath com nível de risco mínimo definido.",
            PerSub(KnightCapability.AzureSecurityContacts, (s, v) => Contact(s, v,
                    c => Sources(c, "AttackPath").Any(x => AzureJson.Str(x, "minimalRiskLevel") is "Critical" or "High" or "Medium" or "Low"),
                    c => Sources(c, "AttackPath").Select(x => AzureJson.Str(x, "minimalRiskLevel")).FirstOrDefault() is { } lvl
                        ? $"caminhos de ataque a partir do risco {lvl}" : "sem notificação de caminhos de ataque",
                    "notificação de caminhos de ataque por nível de risco"),
                "{0} de {1} assinatura(s) não notificam caminhos de ataque.",
                "As {0} assinatura(s) notificam caminhos de ataque."),
            Ref(AzureRefs.Az + "8.1.15")),

        // ======== App Service: referências ao Key Vault (PARCIAL) ============================================
        Custom(KnightService.AzureAppService, "AK-AZ-APP-027", "Referências ao Key Vault não resolvidas nas configurações do App Service", Cloud, SeverityLevel.Medium,
            "Corrigir as referências ao Key Vault que não resolvem (identidade gerenciada, acesso ao cofre, nome ou versão do segredo) e guardar os segredos das configurações no Key Vault.",
            "Toda referência ao Key Vault das configurações do aplicativo com estado Resolved.",
            (c, v) =>
            {
                var (complete, reason) = v.Completeness(KnightCapability.AzureAppService);
                var (refsOk, refsReason) = v.Completeness(KnightCapability.AzureAppSettingsKeyVaultReferences);
                var apps = v.OfType("Microsoft.Web/sites").Concat(v.OfType("Microsoft.Web/sites/slots")).ToList();
                var withRefs = apps.Where(a => a.Items("kvrefs:items").Count > 0).ToList();
                var why = string.Join(" ", new[] { reason, refsReason }.OfType<string>());
                if (apps.Count == 0) return EmptyOrIncomplete(v, KnightCapability.AzureAppService, "aplicativo do App Service");
                if (withRefs.Count == 0)
                    return KnightControlOutcome.NotEvaluated(Trim(complete && refsOk
                        ? $"Nenhum dos {apps.Count} aplicativo(s) referencia o Key Vault nas configurações. Se as demais configurações guardam segredo em texto não é visível com o papel Leitor — a leitura que mostraria isso devolve os valores e não é usada. Registre o resultado manual da referência, se for o caso."
                        : why));
                return Population(withRefs, a =>
                    {
                        var refs = a.Items("kvrefs:items");
                        var bad = refs.Where(r => !string.Equals(AzureJson.Str(r, "properties.status"), "Resolved", StringComparison.OrdinalIgnoreCase)).ToList();
                        return KnightItemCheck.Of(bad.Count == 0,
                            bad.Count == 0 ? $"{refs.Count} referência(s) ao Key Vault resolvida(s)"
                                : $"{bad.Count} de {refs.Count} referência(s) não resolvida(s): " + List(bad.Select(r => $"{AzureJson.Str(r, "name")} ({AzureJson.Str(r, "properties.status") ?? "estado não informado"})"), 4),
                            "referências ao Key Vault resolvidas");
                    }, Describe,
                    (n, t) => $"{n} de {t} aplicativo(s) com referência ao Key Vault têm referência não resolvida.",
                    t => $"Os {t} aplicativo(s) com referência ao Key Vault resolvem todas as referências.",
                    () => KnightControlOutcome.NotEvaluated(Trim(why)),
                    complete && refsOk, why.Length == 0 ? null : Trim(why, 900));
            },
            Ref(AzureRefs.Azc + "2.5", KnightReferenceMatch.Partial,
                "Avalia as configurações que já referenciam o Key Vault (resolução da referência). Se OUTRAS configurações guardam segredo em "
                + "texto não é visível: o método que mostraria isso (config/list) devolve os valores dos segredos e não é usado. Essa parte "
                + "depende de verificação manual.")),

        // ======== Databricks: API do workspace (habilitada em Integrações) ====================================
        Custom(KnightService.AzureDatabricks, "AK-AZ-DBR-008", "Workspaces do Databricks sem Unity Catalog", Cloud, SeverityLevel.Medium,
            "Atribuir um metastore do Unity Catalog a cada workspace do Databricks.",
            "Metastore do Unity Catalog atribuído ao workspace.",
            (c, v) => DbxEvaluate(v, w =>
                {
                    var (value, missing) = Dbx(w, "metastore");
                    const string expected = "metastore do Unity Catalog atribuído";
                    if (value is not { } m) return KnightItemCheck.Of(null, "não lido: " + missing, expected);
                    var assigned = AzureJson.Bool(m, "assigned");
                    return KnightItemCheck.Of(assigned, assigned == true ? "metastore atribuído" : "nenhum metastore atribuído", expected);
                },
                "{0} de {1} workspace(s) do Databricks não têm Unity Catalog.",
                "Os {0} workspace(s) do Databricks têm metastore do Unity Catalog."),
            Ref(AzureRefs.Az + "2.1.5")),

        Custom(KnightService.AzureDatabricks, "AK-AZ-DBR-009", "Tokens pessoais do Databricks sem restrição ou sem expiração", Cloud, SeverityLevel.Medium,
            "Restringir o uso de tokens pessoais a grupos definidos (não ao grupo users) e definir o tempo de vida máximo dos tokens — ou desabilitá-los.",
            "Tokens pessoais desabilitados, ou uso restrito (grupo users sem permissão) e tempo de vida máximo definido.",
            (c, v) => DbxEvaluate(v, w =>
                {
                    const string expected = "tokens desabilitados, ou uso restrito e tempo de vida máximo definido";
                    var (conf, cm) = Dbx(w, "conf");
                    if (conf is not { } cf) return KnightItemCheck.Of(null, "não lido: " + cm, expected);
                    if (string.Equals(AzureJson.Str(cf, "enableTokensConfig"), "false", StringComparison.OrdinalIgnoreCase))
                        return KnightItemCheck.Of(true, "tokens pessoais desabilitados no workspace", expected);
                    var (perms, pm) = Dbx(w, "tokenPerms");
                    var max = AzureJson.Str(cf, "maxTokenLifetimeDays");
                    var hasMax = int.TryParse(max, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0;
                    if (perms is not { } p) return KnightItemCheck.Of(hasMax ? null : false,
                        $"tempo de vida máximo: {(hasMax ? days + " dia(s)" : "não definido")}; permissões de uso não lidas: {pm}", expected);
                    var everyone = AzureJson.Bool(p, "usersGroupCanUse") == true;
                    return KnightItemCheck.Of(hasMax && !everyone,
                        $"tempo de vida máximo: {(hasMax ? days + " dia(s)" : "não definido")}; uso por todos os usuários (grupo users): {YesNo(everyone)}", expected);
                },
                "{0} de {1} workspace(s) do Databricks não restringem ou não expiram os tokens pessoais.",
                "Os {0} workspace(s) do Databricks restringem e expiram os tokens pessoais (ou os desabilitam)."),
            Ref(AzureRefs.Az + "2.1.6")),

        Custom(KnightService.AzureDatabricks, "AK-AZ-DBR-010", "Usuários e grupos do Databricks fora da sincronização com o Entra ID", Cloud, SeverityLevel.Medium,
            "Provisionar usuários e grupos do Databricks a partir do Microsoft Entra ID (provisionamento SCIM ou gerenciamento automático de identidades), sem contas criadas só no workspace.",
            "Todo usuário e grupo do workspace com vínculo externo (externalId) do provisionamento.",
            (c, v) => DbxEvaluate(v, w =>
                {
                    const string expected = "usuários e grupos com vínculo externo do provisionamento";
                    var (users, um) = Dbx(w, "scimUsers");
                    var (groups, gm) = Dbx(w, "scimGroups");
                    if (users is not { } u || groups is not { } g)
                        return KnightItemCheck.Of(null, "não lido: " + (um ?? gm), expected);
                    long Local(JsonElement x) => (AzureJson.Int(x, "read") ?? 0) - (AzureJson.Int(x, "external") ?? 0);
                    var truncated = AzureJson.Bool(u, "truncated") == true || AzureJson.Bool(g, "truncated") == true;
                    var local = Local(u) + Local(g);
                    var names = AzureJson.Items(u, "localNames").Concat(AzureJson.Items(g, "localNames")).Select(x => x.GetString() ?? "").ToList();
                    var found = $"{AzureJson.Int(u, "external") ?? 0} de {AzureJson.Int(u, "read") ?? 0} usuário(s) e {AzureJson.Int(g, "external") ?? 0} de {AzureJson.Int(g, "read") ?? 0} grupo(s) com vínculo externo"
                                + (local > 0 ? $"; sem vínculo: {List(names, 5)}" : "") + (truncated ? "; lista truncada" : "");
                    return KnightItemCheck.Of(local > 0 ? false : truncated ? null : true, found, expected);
                },
                "{0} de {1} workspace(s) do Databricks têm usuários ou grupos fora da sincronização com o Entra ID.",
                "Os {0} workspace(s) do Databricks só têm usuários e grupos provisionados a partir do Entra ID."),
            Ref(AzureRefs.Az + "2.1.4", KnightReferenceMatch.Partial,
                "Verifica o vínculo externo (externalId) de cada usuário e grupo do workspace, que o provisionamento a partir do Entra ID preenche; "
                + "o gerenciamento automático de identidades no nível da CONTA do Databricks não é lido, e uma conta local legítima (de serviço, "
                + "por exemplo) aparece como não sincronizada para decisão humana.")),

        Custom(KnightService.AzureDatabricks, "AK-AZ-DBR-011", "Clusters do Databricks sem criptografia do tráfego entre nós", Cloud, SeverityLevel.Medium,
            "Habilitar a criptografia do tráfego entre os nós de trabalho (spark.network.crypto.enabled e chave compartilhada) por script de inicialização ou política de cluster.",
            "Configuração Spark dos clusters com spark.network.crypto.enabled = true.",
            (c, v) => DbxEvaluate(v, w =>
                {
                    const string expected = "criptografia de rede do Spark ligada em todos os clusters";
                    var (clusters, cm) = Dbx(w, "clusters");
                    if (clusters is not { } cl) return KnightItemCheck.Of(null, "não lido: " + cm, expected);
                    var list = cl.EnumerateArray().ToList();
                    var without = list.Where(x => AzureJson.Bool(x, "networkCrypto") != true).ToList();
                    if (without.Count == 0) return KnightItemCheck.Of(true, $"{list.Count} cluster(s) com criptografia de rede ligada", expected);
                    var (global, _) = Dbx(w, "globalInit");
                    var globalScripts = global is { } gl ? AzureJson.Int(gl, "enabled") ?? 0 : -1;
                    var byScript = without.Where(x => (AzureJson.Int(x, "initScripts") ?? 0) > 0).ToList();
                    var names = List(without.Select(x => AzureJson.Str(x, "name") ?? "?"), 4);
                    // O script de inicialização é o método documentado e o conteúdo dele não é lido: com script, desconhecido.
                    if (byScript.Count == without.Count || globalScripts != 0)
                        return KnightItemCheck.Of(null, $"{without.Count} cluster(s) sem a opção na configuração Spark ({names}), mas com script de inicialização "
                            + (globalScripts > 0 ? "global " : globalScripts < 0 ? "(scripts globais não lidos) " : "") + "— o conteúdo dos scripts não é lido", expected);
                    return KnightItemCheck.Of(false, $"{without.Count} de {list.Count} cluster(s) sem criptografia de rede e sem script de inicialização: {names}", expected);
                },
                "{0} de {1} workspace(s) do Databricks têm clusters sem criptografia do tráfego entre nós.",
                "Os {0} workspace(s) do Databricks criptografam o tráfego entre os nós dos clusters.",
                skip: w => Dbx(w, "clusters").Value is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() == 0,
                emptyText: "nenhum cluster nos workspaces do Databricks do escopo."),
            Ref(AzureRefs.Az + "2.1.3", KnightReferenceMatch.Partial,
                "Lê a configuração Spark de cada cluster (spark.network.crypto.enabled). O método documentado é um script de inicialização, "
                + "cujo conteúdo o AEGIS não lê (pode conter a chave compartilhada): cluster que depende de script fica desconhecido, nunca aprovado.")),
    };
}
