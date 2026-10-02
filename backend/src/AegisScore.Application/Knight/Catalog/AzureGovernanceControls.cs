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
//  [AEGIS-KNIGHT-COVERAGE-04] Controles do Azure: assinaturas e IAM, Azure Monitor e Defender para Nuvem
// ============================================================================
// Controles ORIGINAIS do AEGIS sobre a configuração lida pelo Azure Resource Manager (papel Leitor). Os que olham a
// ASSINATURA (planos do Defender, alertas, proprietários) têm a assinatura como item da população: uma assinatura sem o
// item exigido é o afetado, e aprovar exige todas as assinaturas do escopo lidas.

internal static class AzureRefs
{
    public const string Az = "CIS-AZ-6.0.0:";
    public const string Azc = "CIS-AZC-2.0.0:";
    public const string Azd = "CIS-AZD-2.0.0:";
}

/// <summary>Leituras comuns aos controles de governança.</summary>
internal static class AzureGov
{
    public const string OwnerRole = "8e3af657-a8ff-443c-a75c-2fe8c4bcb635";
    public const string UserAccessAdministratorRole = "18d7d88d-d35e-4fb5-a5c3-7773c20a72d9";

    public static bool HasRole(AzureResource assignment, string roleGuid) =>
        assignment.Str("properties.roleDefinitionId")?.EndsWith("/" + roleGuid, StringComparison.OrdinalIgnoreCase) == true;

    public static IEnumerable<AzureResource> Assignments(AzureView v) => v.OfType("Microsoft.Authorization/roleAssignments");

    /// <summary>Nome legível do principal: o usuário lido no Graph, quando houver; senão o tipo e o identificador.</summary>
    public static (string Name, string? Upn) Principal(AzureView v, AzureResource assignment)
    {
        var id = assignment.Str("properties.principalId") ?? "?";
        var user = v.ById("graph:users/" + id);
        var type = assignment.Str("properties.principalType") switch
        {
            "User" => "usuário",
            "Group" => "grupo",
            "ServicePrincipal" => "aplicação",
            "ForeignGroup" => "grupo externo",
            var t => t ?? "principal",
        };
        return user?.Str("displayName") is { } dn ? (dn, user.Str("userPrincipalName")) : ($"{type} {id}", null);
    }

    /// <summary>Operações de um alerta do log de atividades (todas as comparações "campo = valor" da condição).</summary>
    public static IEnumerable<(string Field, string Value)> Conditions(AzureResource alert)
    {
        IEnumerable<(string, string)> Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in e.EnumerateArray())
                foreach (var y in Walk(x)) yield return y;
            }
            else if (e.ValueKind == JsonValueKind.Object)
            {
                var field = AzureJson.Str(e, "field");
                var eq = AzureJson.Str(e, "equals");
                if (field is not null && eq is not null) yield return (field, eq);
                foreach (var p in e.EnumerateObject())
                    if (p.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                        foreach (var y in Walk(p.Value)) yield return y;
            }
        }
        return alert.Get("properties.condition") is { } c ? Walk(c) : Array.Empty<(string, string)>();
    }

    /// <summary>A assinatura tem um alerta HABILITADO, com escopo nela, que dispara para a condição pedida?</summary>
    public static KnightItemCheck ActivityAlert(AzureResource sub, AzureView v, string field, string value, string what)
    {
        var path = "/subscriptions/" + sub.SubscriptionId;
        var alerts = v.InSubscription("Microsoft.Insights/activityLogAlerts", sub.SubscriptionId)
            .Where(a => Conditions(a).Any(x => string.Equals(x.Field, field, StringComparison.OrdinalIgnoreCase)
                                               && string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var good = alerts.Where(a => a.Bool("properties.enabled") != false
                                     && a.Strings("properties.scopes").Any(s => string.Equals(s.TrimEnd('/'), path, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (good.Count > 0)
        {
            var groups = good.Sum(a => a.Items("properties.actions.actionGroups").Count);
            return KnightItemCheck.Of(true, $"alerta “{good[0].Name}” habilitado no escopo da assinatura ({groups} grupo(s) de ações)",
                $"alerta habilitado para {what} no escopo da assinatura");
        }
        return KnightItemCheck.Of(false,
            alerts.Count > 0 ? $"{alerts.Count} alerta(s) para {what}, mas desabilitado(s) ou com escopo menor que a assinatura" : $"nenhum alerta para {what}",
            $"alerta habilitado para {what} no escopo da assinatura");
    }

    /// <summary>Plano do Defender para Nuvem na assinatura.</summary>
    public static KnightItemCheck Plan(AzureResource sub, AzureView v, string plan, string label)
    {
        var p = v.InSubscription("Microsoft.Security/pricings", sub.SubscriptionId)
            .FirstOrDefault(x => string.Equals(x.Name, plan, StringComparison.OrdinalIgnoreCase));
        if (p is null) return KnightItemCheck.Of(null, $"{label}: plano não devolvido pela API", $"{label}: Standard (ligado)");
        var tier = p.Str("properties.pricingTier");
        var sub2 = p.Str("properties.subPlan");
        return KnightItemCheck.Of(tier is null ? null : string.Equals(tier, "Standard", StringComparison.OrdinalIgnoreCase),
            $"{label}: {(tier is null ? "não informado" : string.Equals(tier, "Standard", StringComparison.OrdinalIgnoreCase) ? "ligado" : "desligado")}"
            + (sub2 is null ? "" : $" (subplano {sub2})"),
            $"{label}: ligado (Standard)");
    }

    /// <summary>Extensão de um plano do Defender (ex.: varredura sem agente do plano de servidores).</summary>
    public static KnightItemCheck PlanExtension(AzureResource sub, AzureView v, string plan, string extension, string label)
    {
        var p = v.InSubscription("Microsoft.Security/pricings", sub.SubscriptionId)
            .FirstOrDefault(x => string.Equals(x.Name, plan, StringComparison.OrdinalIgnoreCase));
        if (p is null) return KnightItemCheck.Of(null, $"{label}: plano não devolvido pela API", $"{label}: ligado");
        if (!string.Equals(p.Str("properties.pricingTier"), "Standard", StringComparison.OrdinalIgnoreCase))
            return KnightItemCheck.Of(false, $"{label}: o plano que o contém está desligado", $"{label}: ligado");
        var ext = p.Items("properties.extensions")
            .FirstOrDefault(e => string.Equals(AzureJson.Str(e, "name"), extension, StringComparison.OrdinalIgnoreCase));
        if (ext.ValueKind != JsonValueKind.Object)
            return KnightItemCheck.Of(false, $"{label}: extensão ausente do plano", $"{label}: ligado");
        var on = AzureJson.Bool(ext, "isEnabled");
        return KnightItemCheck.Of(on, $"{label}: {(on is null ? "não informado" : on.Value ? "ligado" : "desligado")}", $"{label}: ligado");
    }
}

public static class AzureGovernanceControls
{
    private const KnightIndicatorCategory Cloud = KnightIndicatorCategory.CloudInfrastructure;

    private static AzureCheck PerSubscription(KnightCapability family, Func<AzureResource, AzureView, KnightItemCheck> check,
        string exposed, string passed) =>
        new(family, "assinatura", v => v.SubscriptionItems, check, exposed, passed);

    // ---- Planos do Defender para Nuvem (um controle por plano) ------------------------------------------------
    private static KnightIndicatorDefinition PlanControl(string id, string plan, string label, string refKey) =>
        Control(KnightService.DefenderForCloud, id, $"{label} desligado em assinaturas do escopo", Cloud, SeverityLevel.Medium,
            $"Ligar o plano {label} do Microsoft Defender para Nuvem (camada Standard) em todas as assinaturas que têm recursos do tipo protegido.",
            $"Plano “{plan}” do Defender para Nuvem com camada Standard em cada assinatura.",
            PerSubscription(KnightCapability.AzureDefenderForCloud, (s, v) => AzureGov.Plan(s, v, plan, label),
                "{0} de {1} assinatura(s) estão com o " + label + " desligado.",
                "As {0} assinatura(s) do escopo estão com o " + label + " ligado."),
            Ref(AzureRefs.Az + refKey));

    // ---- Alertas do log de atividades (um controle por operação) ---------------------------------------------
    private static KnightIndicatorDefinition AlertControl(string id, string op, string what, string refKey) =>
        Control(KnightService.AzureMonitor, id, $"Sem alerta do log de atividades para {what}", Cloud, SeverityLevel.Medium,
            $"Criar, no escopo de cada assinatura, um alerta do log de atividades habilitado para {what}, com um grupo de ações que avise a equipe responsável.",
            $"Alerta do log de atividades habilitado, com escopo na assinatura, para a operação {op}.",
            PerSubscription(KnightCapability.AzureMonitor, (s, v) => AzureGov.ActivityAlert(s, v, "operationName", op, what),
                "{0} de {1} assinatura(s) não têm alerta habilitado para " + what + ".",
                "As {0} assinatura(s) têm alerta habilitado para " + what + "."),
            Ref(AzureRefs.Az + refKey));

    public static IReadOnlyList<KnightIndicatorDefinition> Definitions { get; } = new[]
    {
        // ======== Assinaturas e IAM ==========================================================================
        Custom(KnightService.AzureSubscription, "AK-AZ-IAM-001", "Administrador de Acesso do Usuário atribuído na raiz do locatário",
            KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.High,
            "Remover as atribuições do papel Administrador de Acesso do Usuário no escopo raiz (“/”), que normalmente restam de uma elevação de acesso, e elevar só quando necessário e por tempo limitado.",
            "Nenhuma atribuição do papel Administrador de Acesso do Usuário no escopo raiz, visível a partir das assinaturas do escopo.",
            (c, v) =>
            {
                var (complete, reason) = v.Completeness(KnightCapability.AzureAuthorization);
                var root = AzureGov.Assignments(v)
                    .Where(a => AzureGov.HasRole(a, AzureGov.UserAccessAdministratorRole) && a.Str("properties.scope") == "/")
                    .ToList();
                var uaaCount = AzureGov.Assignments(v).Count(a => AzureGov.HasRole(a, AzureGov.UserAccessAdministratorRole));
                if (root.Count > 0)
                {
                    var affected = root.Select(a =>
                    {
                        var (name, upn) = AzureGov.Principal(v, a);
                        return new KnightIndicatorObject(KnightObjectRelation.Affected,
                            a.Str("properties.principalType") == "User" ? KnightAffectedObjectKind.User : KnightAffectedObjectKind.ServicePrincipal,
                            Trim(a.Str("properties.principalId") ?? a.Name, 200), Trim(name, 300), upn, new[] { "Administrador de Acesso do Usuário (raiz)" },
                            "Encontrado: papel Administrador de Acesso do Usuário no escopo raiz (“/”). Esperado: nenhuma atribuição na raiz.",
                            "escopo: /");
                    }).ToList();
                    return KnightControlOutcome.Exposed(
                        $"{root.Count} principal(is) mantêm o papel Administrador de Acesso do Usuário na raiz do locatário: podem conceder qualquer acesso a qualquer assinatura.",
                        affected, null, complete, reason);
                }
                return complete
                    ? KnightControlOutcome.Passed($"Nenhuma atribuição do papel Administrador de Acesso do Usuário na raiz; {uaaCount} atribuição(ões) do papel em escopos menores ficam como evidência.")
                    : KnightControlOutcome.NotEvaluated(Trim(reason ?? "leitura incompleta."));
            },
            Ref(AzureRefs.Az + "5.3.3", KnightReferenceMatch.Partial,
                "Avalia a atribuição no escopo raiz (resultado da elevação de acesso), que é a visível a partir das assinaturas com o papel Leitor; atribuições em grupos de gerenciamento sem assinatura no escopo não são vistas.")),

        Custom(KnightService.AzureSubscription, "AK-AZ-IAM-002", "Contas de usuário desabilitadas mantêm papéis no Azure",
            KnightIndicatorCategory.AccountHygiene, SeverityLevel.High,
            "Remover as atribuições de papel do Azure das contas de usuário desabilitadas no diretório.",
            "Toda conta de USUÁRIO com atribuição de papel visível nas assinaturas do escopo está habilitada no Microsoft Entra ID.",
            (c, v) =>
            {
                var (complete, reason) = v.Completeness(KnightCapability.AzureAuthorization);
                var graph = v.TenantRead("graphUsers");
                if (graph is not ("Collected" or null))
                {
                    complete = false;
                    reason = string.Join(" ", new[] { reason, graph == "Truncated"
                        ? "O estado das contas foi lido só até o teto da coleta."
                        : "O estado das contas no Microsoft Graph não foi lido (permissão User.Read.All)." }.Where(x => x is not null));
                }
                var byUser = AzureGov.Assignments(v).Where(a => a.Str("properties.principalType") == "User")
                    .GroupBy(a => a.Str("properties.principalId") ?? "", StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Key.Length > 0).ToList();
                return Population(byUser,
                    g =>
                    {
                        var u = v.ById("graph:users/" + g.Key);
                        var enabled = u?.Bool("accountEnabled");
                        var scopes = List(g.Select(a => a.Str("properties.scope") ?? "?").Distinct(), 3);
                        return KnightItemCheck.Of(enabled,
                            u is null ? $"conta não lida no diretório; {g.Count()} atribuição(ões) em {scopes}"
                            : u.Str("status") == "NotFound" ? $"conta não encontrada no diretório (atribuição órfã); {g.Count()} atribuição(ões) em {scopes}"
                            : $"conta {(enabled == false ? "DESABILITADA" : enabled == true ? "habilitada" : "com estado não informado")}; {g.Count()} atribuição(ões) em {scopes}",
                            "conta habilitada, ou atribuições removidas");
                    },
                    g =>
                    {
                        var (name, upn) = AzureGov.Principal(v, g.First());
                        return new KnightIndicatorObject(KnightObjectRelation.Evidence, KnightAffectedObjectKind.User, Trim(g.Key, 200), Trim(name, 300), upn,
                            Array.Empty<string>(), "", null);
                    },
                    (n, t) => $"{n} de {t} conta(s) de usuário com papel no Azure estão desabilitadas no diretório e mantêm as atribuições.",
                    t => $"As {t} conta(s) de usuário com papel no Azure estão habilitadas.",
                    () => complete
                        ? KnightControlOutcome.NotApplicable("nenhuma conta de usuário tem atribuição de papel visível nas assinaturas do escopo.")
                        : KnightControlOutcome.NotEvaluated(Trim(reason ?? "leitura incompleta.")),
                    complete, reason);
            },
            Ref(AzureRefs.Az + "5.3.5")),

        Control(KnightService.AzureSubscription, "AK-AZ-IAM-003", "Papel personalizado com todas as ações (administrador de assinatura)",
            KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.Medium,
            "Substituir papéis personalizados com a ação “*” por papéis internos ou personalizados com as ações estritamente necessárias.",
            "Nenhum papel personalizado com a ação “*” atribuível a assinaturas do escopo.",
            new AzureCheck(KnightCapability.AzureAuthorization, "papel personalizado",
                v => v.OfType("Microsoft.Authorization/roleDefinitions"),
                (r, _) =>
                {
                    var actions = r.Items("properties.permissions").SelectMany(p => AzureJson.Items(p, "actions"))
                        .Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).ToList();
                    var star = actions.Contains("*");
                    return KnightItemCheck.Of(!star, $"ações: {List(actions, 4)}; escopos atribuíveis: {List(r.Strings("properties.assignableScopes"), 2)}",
                        "sem a ação “*”");
                },
                "{0} de {1} papel(éis) personalizado(s) concedem todas as ações (“*”), como um proprietário.",
                "Nenhum dos {0} papel(éis) personalizado(s) concede todas as ações."),
            Ref(AzureRefs.Az + "5.4")),

        Control(KnightService.AzureSubscription, "AK-AZ-IAM-004", "Sem papel personalizado dedicado à administração de bloqueios de recurso",
            KnightIndicatorCategory.IdentityGovernance, SeverityLevel.Medium,
            "Criar um papel personalizado com as ações de bloqueio (Microsoft.Authorization/locks/*), atribuível à assinatura, para que só quem administra bloqueios possa removê-los.",
            "Em cada assinatura, ao menos um papel personalizado atribuível com permissão sobre Microsoft.Authorization/locks, sem a ação “*”.",
            PerSubscription(KnightCapability.AzureAuthorization, (s, v) =>
                {
                    var path = "/subscriptions/" + s.SubscriptionId;
                    var roles = v.OfType("Microsoft.Authorization/roleDefinitions")
                        .Where(r => string.Equals(r.SubscriptionId, s.SubscriptionId, StringComparison.OrdinalIgnoreCase)
                                    || r.Strings("properties.assignableScopes").Any(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase)))
                        .Where(r =>
                        {
                            var acts = r.Items("properties.permissions").SelectMany(p => AzureJson.Items(p, "actions"))
                                .Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).ToList();
                            return !acts.Contains("*") && acts.Any(a => a.StartsWith("Microsoft.Authorization/locks", StringComparison.OrdinalIgnoreCase));
                        })
                        .Select(r => r.Str("properties.roleName") ?? r.Name).ToList();
                    return KnightItemCheck.Of(roles.Count > 0,
                        roles.Count > 0 ? $"papel(éis) de bloqueio: {List(roles, 3)}" : "nenhum papel personalizado de administração de bloqueios",
                        "papel personalizado com permissão sobre bloqueios de recurso");
                },
                "{0} de {1} assinatura(s) não têm papel personalizado para administrar bloqueios.",
                "As {0} assinatura(s) têm papel personalizado para administrar bloqueios."),
            Ref(AzureRefs.Az + "5.5")),

        Custom(KnightService.AzureSubscription, "AK-AZ-IAM-005", "Assinaturas podem sair do locatário ou entrar nele sem restrição",
            KnightIndicatorCategory.TenantConfiguration, SeverityLevel.Medium,
            "Bloquear, na política de assinaturas do locatário, a saída de assinaturas para outro diretório e a entrada de assinaturas de outros diretórios, com isenções nominais.",
            "Política de assinaturas do locatário: saída e entrada de assinaturas bloqueadas.",
            (c, v) =>
            {
                var p = v.ById("/providers/Microsoft.Subscription/policies/default");
                if (p is null)
                    return KnightControlOutcome.NotEvaluated(
                        c.Capabilities.FirstOrDefault(x => x.Capability == KnightCapability.AzureAuthorization)?.Detail
                        ?? "a política de assinaturas do locatário não foi lida nesta coleta.");
                var leaving = p.Bool("properties.blockSubscriptionsLeavingTenant");
                var entering = p.Bool("properties.blockSubscriptionsIntoTenant");
                var exempt = p.Strings("properties.exemptedPrincipals").Count;
                bool? ok = leaving is null || entering is null ? null : leaving.Value && entering.Value;
                return Setting(ok, "subscription-policy", "Política de assinaturas do locatário",
                    $"saída {(leaving switch { true => "bloqueada", false => "permitida", _ => "não informada" })}, entrada {(entering switch { true => "bloqueada", false => "permitida", _ => "não informada" })}, {exempt} isenção(ões)",
                    "saída e entrada bloqueadas",
                    "Assinaturas podem ser transferidas para outro diretório (levando recursos e dados para fora da governança da organização) ou trazidas de outro diretório sem controle.",
                    "A saída e a entrada de assinaturas estão bloqueadas na política do locatário.");
            },
            Ref(AzureRefs.Az + "5.6")),

        Control(KnightService.AzureSubscription, "AK-AZ-IAM-006", "Número de proprietários da assinatura fora da faixa de dois a três",
            KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.High,
            "Manter de dois a três principais com o papel Proprietário atribuído diretamente na assinatura: ao menos dois para continuidade, não mais que três para limitar o acesso pleno.",
            "Atribuições do papel Proprietário no escopo da assinatura: entre 2 e 3 principais distintos.",
            PerSubscription(KnightCapability.AzureAuthorization, (s, v) =>
                {
                    var path = "/subscriptions/" + s.SubscriptionId;
                    var owners = AzureGov.Assignments(v)
                        .Where(a => AzureGov.HasRole(a, AzureGov.OwnerRole)
                                    && string.Equals(a.Str("properties.scope")?.TrimEnd('/'), path, StringComparison.OrdinalIgnoreCase))
                        .Select(a => a.Str("properties.principalId")).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                    return KnightItemCheck.Of(owners is >= 2 and <= 3, $"{owners} proprietário(s) atribuído(s) na assinatura", "de 2 a 3 proprietários");
                },
                "{0} de {1} assinatura(s) têm número de proprietários fora da faixa de dois a três.",
                "As {0} assinatura(s) têm de dois a três proprietários."),
            Ref(AzureRefs.Az + "5.7")),

        // ======== Azure Monitor ==============================================================================
        Control(KnightService.AzureMonitor, "AK-AZ-MON-001", "Grupos de segurança de rede sem log de fluxo enviado ao Log Analytics",
            Cloud, SeverityLevel.Low,
            "Configurar, no Network Watcher, log de fluxo para cada NSG com a análise de tráfego habilitada num workspace do Log Analytics (ou migrar para logs de fluxo de rede virtual).",
            "Cada NSG é alvo de um log de fluxo habilitado com análise de tráfego (Log Analytics).",
            new AzureCheck(KnightCapability.AzureNetworking, "grupo de segurança de rede",
                v => v.OfType("Microsoft.Network/networkSecurityGroups"),
                (r, v) => AzureNet.FlowLogToWorkspace(r, v),
                "{0} de {1} NSG(s) não têm log de fluxo enviado ao Log Analytics.",
                "Os {0} NSG(s) têm log de fluxo com análise de tráfego no Log Analytics."),
            Ref(AzureRefs.Az + "6.1.1.5")),

        Control(KnightService.AzureMonitor, "AK-AZ-MON-002", "Redes virtuais sem log de fluxo enviado ao Log Analytics",
            Cloud, SeverityLevel.Medium,
            "Configurar log de fluxo de rede virtual no Network Watcher para cada VNet, com a análise de tráfego habilitada num workspace do Log Analytics.",
            "Cada rede virtual é alvo de um log de fluxo habilitado com análise de tráfego (Log Analytics).",
            new AzureCheck(KnightCapability.AzureNetworking, "rede virtual",
                v => v.OfType("Microsoft.Network/virtualNetworks"),
                (r, v) => AzureNet.FlowLogToWorkspace(r, v),
                "{0} de {1} rede(s) virtual(is) não têm log de fluxo enviado ao Log Analytics.",
                "As {0} rede(s) virtual(is) têm log de fluxo com análise de tráfego no Log Analytics."),
            Ref(AzureRefs.Az + "6.1.1.6")),

        Custom(KnightService.AzureMonitor, "AK-AZ-MON-003", "Logs de atividade do Microsoft Graph não exportados",
            Cloud, SeverityLevel.Medium,
            "Criar, nas configurações de diagnóstico do Microsoft Entra ID, uma configuração que envie a categoria MicrosoftGraphActivityLogs a um destino retido (Log Analytics, armazenamento ou hub de eventos).",
            "Configuração de diagnóstico do Entra ID com a categoria MicrosoftGraphActivityLogs habilitada e um destino.",
            (c, v) => AzureMonitorRules.EntraCategories(c, v, new[] { "MicrosoftGraphActivityLogs" },
                "MicrosoftGraphActivityLogs", "As chamadas feitas à API do Microsoft Graph no locatário não ficam registradas fora do Entra: leitura em massa de dados por uma aplicação comprometida não deixa rastro consultável."),
            Ref(AzureRefs.Az + "6.1.1.7")),

        Custom(KnightService.AzureMonitor, "AK-AZ-MON-004", "Logs de auditoria e de entrada do Entra ID não exportados para retenção",
            Cloud, SeverityLevel.Medium,
            "Enviar, por configuração de diagnóstico do Entra ID, as categorias de auditoria e de entrada (interativas, não interativas, de aplicações e de identidades gerenciadas) a um workspace do Log Analytics.",
            "Categorias AuditLogs, SignInLogs, NonInteractiveUserSignInLogs, ServicePrincipalSignInLogs e ManagedIdentitySignInLogs habilitadas com destino.",
            (c, v) => AzureMonitorRules.EntraCategories(c, v,
                new[] { "AuditLogs", "SignInLogs", "NonInteractiveUserSignInLogs", "ServicePrincipalSignInLogs", "ManagedIdentitySignInLogs" },
                "auditoria e entradas do Entra ID", "Os registros de auditoria e de entrada ficam só na retenção nativa do Entra (de 7 a 30 dias, conforme a licença): a investigação de um incidente antigo não encontra a trilha."),
            Ref(AzureRefs.Az + "6.1.1.8", KnightReferenceMatch.Partial,
                "O AEGIS exige as cinco categorias de auditoria e de entrada com qualquer destino retido; categorias de risco dependem de licença e ficam como evidência.")),

        AlertControl("AK-AZ-MON-005", "Microsoft.Authorization/policyAssignments/write", "criação de atribuição de política", "6.1.2.1"),
        AlertControl("AK-AZ-MON-006", "Microsoft.Authorization/policyAssignments/delete", "exclusão de atribuição de política", "6.1.2.2"),
        AlertControl("AK-AZ-MON-007", "Microsoft.Network/networkSecurityGroups/write", "criação ou alteração de NSG", "6.1.2.3"),
        AlertControl("AK-AZ-MON-008", "Microsoft.Network/networkSecurityGroups/delete", "exclusão de NSG", "6.1.2.4"),
        AlertControl("AK-AZ-MON-009", "Microsoft.Security/securitySolutions/write", "criação ou alteração de solução de segurança", "6.1.2.5"),
        AlertControl("AK-AZ-MON-010", "Microsoft.Security/securitySolutions/delete", "exclusão de solução de segurança", "6.1.2.6"),
        AlertControl("AK-AZ-MON-011", "Microsoft.Sql/servers/firewallRules/write", "criação ou alteração de regra de firewall do SQL", "6.1.2.7"),
        AlertControl("AK-AZ-MON-012", "Microsoft.Sql/servers/firewallRules/delete", "exclusão de regra de firewall do SQL", "6.1.2.8"),
        AlertControl("AK-AZ-MON-013", "Microsoft.Network/publicIPAddresses/write", "criação ou alteração de IP público", "6.1.2.9"),
        AlertControl("AK-AZ-MON-014", "Microsoft.Network/publicIPAddresses/delete", "exclusão de IP público", "6.1.2.10"),

        Control(KnightService.AzureMonitor, "AK-AZ-MON-015", "Sem alerta de integridade do serviço (Service Health)", Cloud, SeverityLevel.Medium,
            "Criar, no escopo de cada assinatura, um alerta do log de atividades da categoria Service Health, com grupo de ações.",
            "Alerta do log de atividades habilitado, com escopo na assinatura, para a categoria ServiceHealth.",
            PerSubscription(KnightCapability.AzureMonitor, (s, v) => AzureGov.ActivityAlert(s, v, "category", "ServiceHealth", "eventos de integridade do serviço"),
                "{0} de {1} assinatura(s) não têm alerta de integridade do serviço.",
                "As {0} assinatura(s) têm alerta de integridade do serviço."),
            Ref(AzureRefs.Az + "6.1.2.11")),

        Control(KnightService.AzureMonitor, "AK-AZ-MON-016", "Assinaturas sem Application Insights", Cloud, SeverityLevel.Medium,
            "Configurar o Application Insights para as aplicações hospedadas na assinatura, de modo que falhas e acessos anômalos fiquem registrados.",
            "Ao menos um recurso do Application Insights em cada assinatura.",
            PerSubscription(KnightCapability.AzureMonitor, (s, v) =>
                {
                    var n = v.InSubscription("Microsoft.Insights/components", s.SubscriptionId).Count();
                    return KnightItemCheck.Of(n > 0, $"{n} recurso(s) do Application Insights", "ao menos um recurso do Application Insights");
                },
                "{0} de {1} assinatura(s) não têm nenhum recurso do Application Insights.",
                "As {0} assinatura(s) têm Application Insights."),
            Ref(AzureRefs.Az + "6.1.3.1")),

        // ======== Defender para Nuvem ========================================================================
        PlanControl("AK-AZ-MDC-001", "CloudPosture", "Defender CSPM", "8.1.1.1"),
        PlanControl("AK-AZ-MDC-002", "Api", "Defender para APIs", "8.1.2.1"),
        PlanControl("AK-AZ-MDC-003", "VirtualMachines", "Defender para Servidores", "8.1.3.1"),
        PlanControl("AK-AZ-MDC-004", "Containers", "Defender para Contêineres", "8.1.4.1"),
        PlanControl("AK-AZ-MDC-005", "StorageAccounts", "Defender para Armazenamento", "8.1.5.1"),
        PlanControl("AK-AZ-MDC-006", "AppServices", "Defender para App Service", "8.1.6.1"),
        PlanControl("AK-AZ-MDC-007", "CosmosDbs", "Defender para Cosmos DB", "8.1.7.1"),
        PlanControl("AK-AZ-MDC-008", "OpenSourceRelationalDatabases", "Defender para bancos relacionais de código aberto", "8.1.7.2"),
        PlanControl("AK-AZ-MDC-009", "SqlServers", "Defender para Azure SQL", "8.1.7.3"),
        PlanControl("AK-AZ-MDC-010", "SqlServerVirtualMachines", "Defender para SQL em máquinas", "8.1.7.4"),
        PlanControl("AK-AZ-MDC-011", "KeyVaults", "Defender para Key Vault", "8.1.8.1"),
        PlanControl("AK-AZ-MDC-012", "Arm", "Defender para Resource Manager", "8.1.9.1"),

        Control(KnightService.DefenderForCloud, "AK-AZ-MDC-013", "Avaliação de vulnerabilidades de máquinas desligada", Cloud, SeverityLevel.Medium,
            "Ligar a avaliação de vulnerabilidades do Microsoft Defender (gerenciamento de vulnerabilidades do Defender) nas configurações de servidores de cada assinatura.",
            "Configuração de avaliação de vulnerabilidades de servidores com o provedor MdeTvm em cada assinatura.",
            PerSubscription(KnightCapability.AzureDefenderForCloud, (s, v) =>
                {
                    var set = v.InSubscription("Microsoft.Security/serverVulnerabilityAssessmentsSettings", s.SubscriptionId).ToList();
                    var provider = set.Select(x => x.Str("properties.selectedProvider")).FirstOrDefault(x => x is not null);
                    return KnightItemCheck.Of(string.Equals(provider, "MdeTvm", StringComparison.OrdinalIgnoreCase),
                        set.Count == 0 ? "nenhuma configuração de avaliação de vulnerabilidades" : $"provedor: {provider ?? "não informado"}",
                        "provedor MdeTvm (gerenciamento de vulnerabilidades do Defender)");
                },
                "{0} de {1} assinatura(s) estão sem a avaliação de vulnerabilidades de máquinas.",
                "As {0} assinatura(s) têm a avaliação de vulnerabilidades de máquinas ligada."),
            Ref(AzureRefs.Az + "8.1.3.2")),

        Control(KnightService.DefenderForCloud, "AK-AZ-MDC-014", "Integração com o Defender para Endpoint desligada", Cloud, SeverityLevel.Medium,
            "Ligar a integração WDATP (Microsoft Defender para Endpoint) nas configurações do Defender para Nuvem de cada assinatura.",
            "Configuração WDATP do Defender para Nuvem habilitada em cada assinatura.",
            PerSubscription(KnightCapability.AzureDefenderForCloud, (s, v) =>
                {
                    var w = v.InSubscription("Microsoft.Security/settings", s.SubscriptionId)
                        .FirstOrDefault(x => string.Equals(x.Name, "WDATP", StringComparison.OrdinalIgnoreCase));
                    var on = w?.Bool("properties.enabled");
                    return KnightItemCheck.Of(w is null ? null : on,
                        w is null ? "configuração WDATP não devolvida" : $"integração WDATP: {(on is null ? "não informada" : on.Value ? "ligada" : "desligada")}",
                        "integração WDATP ligada");
                },
                "{0} de {1} assinatura(s) estão com a integração com o Defender para Endpoint desligada.",
                "As {0} assinatura(s) têm a integração com o Defender para Endpoint ligada."),
            Ref(AzureRefs.Az + "8.1.3.3")),

        Control(KnightService.DefenderForCloud, "AK-AZ-MDC-015", "Varredura sem agente de máquinas desligada", Cloud, SeverityLevel.Medium,
            "Ligar a extensão de varredura sem agente (AgentlessVmScanning) do plano Defender para Servidores em cada assinatura.",
            "Extensão AgentlessVmScanning habilitada no plano VirtualMachines.",
            PerSubscription(KnightCapability.AzureDefenderForCloud, (s, v) => AzureGov.PlanExtension(s, v, "VirtualMachines", "AgentlessVmScanning", "varredura sem agente"),
                "{0} de {1} assinatura(s) estão sem a varredura sem agente de máquinas.",
                "As {0} assinatura(s) têm a varredura sem agente de máquinas ligada."),
            Ref(AzureRefs.Az + "8.1.3.4")),

        Control(KnightService.DefenderForCloud, "AK-AZ-MDC-016", "Monitoramento de integridade de arquivos desligado", Cloud, SeverityLevel.Medium,
            "Ligar a extensão de monitoramento de integridade de arquivos (FileIntegrityMonitoring) do plano Defender para Servidores em cada assinatura.",
            "Extensão FileIntegrityMonitoring habilitada no plano VirtualMachines.",
            PerSubscription(KnightCapability.AzureDefenderForCloud, (s, v) => AzureGov.PlanExtension(s, v, "VirtualMachines", "FileIntegrityMonitoring", "monitoramento de integridade de arquivos"),
                "{0} de {1} assinatura(s) estão sem monitoramento de integridade de arquivos.",
                "As {0} assinatura(s) têm o monitoramento de integridade de arquivos ligado."),
            Ref(AzureRefs.Az + "8.1.3.5")),

        Control(KnightService.DefenderForCloud, "AK-AZ-MDC-017", "Políticas do Microsoft Cloud Security Benchmark desabilitadas", Cloud, SeverityLevel.Medium,
            "Atribuir a iniciativa padrão do Defender para Nuvem (Microsoft Cloud Security Benchmark) a cada assinatura e não definir parâmetros de efeito como “Disabled”.",
            "Atribuição SecurityCenterBuiltIn presente na assinatura, sem parâmetro de efeito com valor Disabled.",
            PerSubscription(KnightCapability.AzurePolicy, (s, v) =>
                {
                    var a = v.InSubscription("Microsoft.Authorization/policyAssignments", s.SubscriptionId)
                        .FirstOrDefault(x => string.Equals(x.Name, "SecurityCenterBuiltIn", StringComparison.OrdinalIgnoreCase));
                    if (a is null) return KnightItemCheck.Of(false, "iniciativa padrão não atribuída à assinatura", "iniciativa atribuída, sem efeitos desabilitados");
                    var disabled = new List<string>();
                    if (a.Get("properties.parameters") is { ValueKind: JsonValueKind.Object } ps)
                        foreach (var p in ps.EnumerateObject())
                            if (string.Equals(AzureJson.Str(p.Value, "value"), "Disabled", StringComparison.OrdinalIgnoreCase)) disabled.Add(p.Name);
                    return KnightItemCheck.Of(disabled.Count == 0,
                        disabled.Count == 0 ? "iniciativa atribuída, nenhum efeito desabilitado" : $"{disabled.Count} efeito(s) desabilitado(s): {List(disabled, 4)}",
                        "iniciativa atribuída, sem efeitos desabilitados");
                },
                "{0} de {1} assinatura(s) têm a iniciativa do Microsoft Cloud Security Benchmark ausente ou com políticas desabilitadas.",
                "As {0} assinatura(s) têm a iniciativa do Microsoft Cloud Security Benchmark sem políticas desabilitadas."),
            Ref(AzureRefs.Az + "8.1.11")),

        Control(KnightService.DefenderForCloud, "AK-AZ-MDC-018", "Gerenciamento da superfície de ataque externa (EASM) não implantado", Cloud, SeverityLevel.Low,
            "Implantar um workspace do Microsoft Defender EASM para inventariar e monitorar os ativos da organização expostos na internet.",
            "Ao menos um workspace do Defender EASM em cada assinatura do escopo.",
            PerSubscription(KnightCapability.AzureDefenderForCloud, (s, v) =>
                {
                    var n = v.InSubscription("Microsoft.Easm/workspaces", s.SubscriptionId).Count();
                    return KnightItemCheck.Of(n > 0, $"{n} workspace(s) do EASM", "ao menos um workspace do EASM");
                },
                "{0} de {1} assinatura(s) não têm workspace do Defender EASM.",
                "As {0} assinatura(s) têm workspace do Defender EASM."),
            Ref(AzureRefs.Az + "8.1.16", KnightReferenceMatch.Partial,
                "Avalia a existência do workspace do Defender EASM em cada assinatura; o locatário pode concentrar o EASM numa assinatura fora do escopo.")),

        Control(KnightService.DefenderForCloud, "AK-AZ-MDC-019", "Hubs IoT sem o Defender para IoT", Cloud, SeverityLevel.Low,
            "Habilitar o Microsoft Defender para IoT nos hubs IoT (solução de segurança de IoT associada).",
            "Assinaturas com hub IoT têm ao menos uma solução de segurança de IoT (Defender para IoT).",
            new AzureCheck(KnightCapability.AzureDefenderForCloud, "assinatura com hub IoT",
                v => v.SubscriptionItems.Where(s => v.InSubscription("Microsoft.Devices/IotHubs", s.SubscriptionId).Any()),
                (s, v) =>
                {
                    var hubs = v.InSubscription("Microsoft.Devices/IotHubs", s.SubscriptionId).Count();
                    var sol = v.InSubscription("Microsoft.Security/iotSecuritySolutions", s.SubscriptionId).Count();
                    return KnightItemCheck.Of(sol > 0, $"{hubs} hub(s) IoT, {sol} solução(ões) do Defender para IoT", "solução do Defender para IoT para os hubs");
                },
                "{0} de {1} assinatura(s) com hub IoT não têm o Defender para IoT.",
                "As {0} assinatura(s) com hub IoT têm o Defender para IoT."),
            Ref(AzureRefs.Az + "8.2.1", KnightReferenceMatch.Partial,
                "Avalia a existência da solução de segurança de IoT na assinatura dos hubs; não confere quais hubs cada solução cobre.")),
    };
}

/// <summary>Regras do Azure Monitor que leem a configuração do locatário.</summary>
internal static class AzureMonitorRules
{
    public static KnightControlOutcome EntraCategories(KnightEvaluationContext c, AzureView v, string[] required, string label, string exposedText)
    {
        var status = v.TenantRead(KnightCapability.AzureTenantDiagnostics.ToString());
        if (status != "Collected")
            return KnightControlOutcome.NotEvaluated(
                c.Capabilities.FirstOrDefault(x => x.Capability == KnightCapability.AzureTenantDiagnostics)?.Detail
                ?? "as configurações de diagnóstico do Microsoft Entra ID não foram lidas nesta coleta.");
        var settings = v.OfType("microsoft.aadiam/diagnosticSettings").ToList();
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in settings)
        {
            var hasDestination = s.Has("properties.workspaceId") || s.Has("properties.storageAccountId") || s.Has("properties.eventHubAuthorizationRuleId");
            if (!hasDestination) continue;
            foreach (var l in s.Items("properties.logs"))
                if (AzureJson.Bool(l, "enabled") == true && AzureJson.Str(l, "category") is { } cat) enabled.Add(cat);
        }
        var missing = required.Where(r => !enabled.Contains(r)).ToList();
        var evidence = settings.Select(s => KnightObjects.Evidence(KnightAffectedObjectKind.TenantSetting, AzureResource.CompactId(s.Id), s.Name,
            $"Destinos: {List(new[] { s.Has("properties.workspaceId") ? "Log Analytics" : null, s.Has("properties.storageAccountId") ? "armazenamento" : null, s.Has("properties.eventHubAuthorizationRuleId") ? "hub de eventos" : null }.OfType<string>())}.",
            Trim("Categorias habilitadas: " + List(s.Items("properties.logs").Where(l => AzureJson.Bool(l, "enabled") == true).Select(l => AzureJson.Str(l, "category") ?? "?"), 12), 2000))).ToList();
        var obj = KnightObjects.Setting("aadiam-diagnostics", "Configurações de diagnóstico do Microsoft Entra ID",
            missing.Count == 0 ? "categorias exigidas habilitadas com destino" : $"sem destino para: {List(missing, 6)}",
            $"categorias {List(required, 6)} habilitadas com destino");
        return missing.Count == 0
            ? KnightControlOutcome.Passed($"As categorias de {label} são enviadas a um destino retido por {settings.Count} configuração(ões) de diagnóstico.", evidence.Prepend(obj).ToList())
            : KnightControlOutcome.Exposed(exposedText + $" Categorias sem destino: {List(missing, 6)}.", Array.Empty<KnightIndicatorObject>(), evidence.Prepend(obj).ToList());
    }
}
