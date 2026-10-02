using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Application.Knight.Reference;
using AegisScore.Domain;

namespace AegisScore.Application.Knight.Catalog;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-04] Kit das regras do Azure
// ============================================================================
// As regras do kit geral valem aqui, com uma dimensão a mais — a ASSINATURA:
//   • a população de um controle (contas de armazenamento, servidores, assinaturas…) só está COMPLETA quando a família de
//     leitura que a produz foi lida em TODAS as assinaturas do escopo, sem atingir o teto. Um recurso reprovado numa
//     assinatura lida continua reprovado; aprovar exige o escopo inteiro;
//   • população vazia com a família completa = NÃO SE APLICA ("nenhuma conta de armazenamento nas 3 assinaturas"); vazia
//     com a família incompleta = NÃO AVALIADO;
//   • fato ausente = desconhecido. Quando a documentação da Microsoft fixa o valor padrão da propriedade omitida, a regra
//     diz isso no texto encontrado ("não definido — padrão documentado: …"); nunca o coletor.

/// <summary>Visão do Azure de UMA coleta: assinaturas do escopo, recursos e completude por família.</summary>
public sealed class AzureView
{
    private static readonly ConditionalWeakTable<KnightTenantConfiguration, AzureView> Cache = new();

    public const string SubscriptionType = "Microsoft.Resources/subscriptions";

    private readonly Dictionary<string, List<AzureResource>> _byType;
    private readonly Dictionary<string, AzureResource> _byId;

    private AzureView(string? missing, IReadOnlyList<AzureSubscriptionInventory> scope, IReadOnlyList<AzureResource> resources,
        IReadOnlyList<KnightCapabilityStatus> capabilities, DateTimeOffset now)
    {
        Now = now;
        Missing = missing;
        Scope = scope;
        Resources = resources;
        Capabilities = capabilities;
        _byType = resources.GroupBy(r => r.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        _byId = new Dictionary<string, AzureResource>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in resources) _byId.TryAdd(r.Id, r);
        SubscriptionItems = scope.Select(s => new AzureResource($"/subscriptions/{s.SubscriptionId}", SubscriptionType,
            s.DisplayName ?? s.SubscriptionId, s.SubscriptionId, null, null, null, KnightCapability.AzureSubscriptions,
            new Dictionary<string, JsonElement>())).ToList();
    }

    /// <summary>Instante da coleta: a régua das regras de idade (rotação de chaves, expiração).</summary>
    public DateTimeOffset Now { get; }

    /// <summary>Motivo pelo qual não há visão (coleta não concluída, nenhuma assinatura no escopo).</summary>
    public string? Missing { get; }

    public IReadOnlyList<AzureSubscriptionInventory> Scope { get; }

    public IReadOnlyList<AzureResource> Resources { get; }

    public IReadOnlyList<KnightCapabilityStatus> Capabilities { get; }

    /// <summary>As assinaturas do escopo como itens de população (controles por assinatura).</summary>
    public IReadOnlyList<AzureResource> SubscriptionItems { get; }

    public static AzureView Of(KnightEvaluationContext c) => Cache.GetValue(c.Configuration, cfg => Build(cfg, c.Capabilities, c.CollectedAt));

    private static AzureView Build(KnightTenantConfiguration cfg, IReadOnlyList<KnightCapabilityStatus> caps, DateTimeOffset now)
    {
        var subs = cfg.Read<AzureSubscriptionInventory>();
        if (!subs.Collected)
            return new AzureView(subs.MissingReason ?? "inventário das assinaturas não coletado.", Array.Empty<AzureSubscriptionInventory>(),
                Array.Empty<AzureResource>(), caps, now);
        var res = cfg.Read<AzureResource>();
        if (!res.Collected)
            return new AzureView(res.MissingReason ?? "recursos do Azure não coletados.", Array.Empty<AzureSubscriptionInventory>(),
                Array.Empty<AzureResource>(), caps, now);
        var scope = subs.Items.Where(s => s.InScope).OrderBy(s => s.SubscriptionId, StringComparer.Ordinal).ToList();
        var missing = scope.Count == 0
            ? "nenhuma assinatura no escopo desta coleta (a aplicação não enxerga assinaturas ou as pedidas não estão visíveis)."
            : null;
        return new AzureView(missing, scope, res.Items, caps, now);
    }

    /// <summary>Item derivado (sub-rede, chave, região em uso): identidade própria e fatos copiados do item de origem.</summary>
    public static AzureResource Derived(AzureResource parent, string id, string type, string name, JsonElement? item = null,
        string? location = null) =>
        new(id, type, name, parent.SubscriptionId, parent.ResourceGroup, location ?? parent.Location, null, parent.Family,
            item is { } e ? new Dictionary<string, JsonElement> { ["item"] = e } : new Dictionary<string, JsonElement>(), parent.Id);

    public IReadOnlyList<AzureResource> OfType(string type) =>
        _byType.TryGetValue(type, out var list) ? list : Array.Empty<AzureResource>();

    public AzureResource? ById(string? id) => id is not null && _byId.TryGetValue(id, out var r) ? r : null;

    public IEnumerable<AzureResource> InSubscription(string type, string subscriptionId) =>
        OfType(type).Where(r => string.Equals(r.SubscriptionId, subscriptionId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Estado de uma leitura de escopo do locatário (marcador gravado pelo coletor), ou null se não houve.</summary>
    public string? TenantRead(string marker) => ById("aegis:tenant/" + marker)?.Str("status");

    /// <summary>A família foi lida em todas as assinaturas do escopo, sem teto? Senão, o porquê.</summary>
    public (bool Complete, string? Reason) Completeness(KnightCapability family)
    {
        var failed = new List<(AzureSubscriptionInventory Sub, AzureFamilyRead? Read)>();
        var truncated = 0;
        foreach (var s in Scope)
        {
            var r = s.ReadOf(family);
            if (r is null || r.Outcome != KnightCapabilityOutcome.Collected) failed.Add((s, r));
            else if (r.Truncated) truncated++;
        }
        if (failed.Count == 0 && truncated == 0) return (true, null);
        var parts = new List<string>();
        if (failed.Count > 0)
        {
            var first = failed[0];
            parts.Add($"A leitura de {AzureLabels.Family(family)} não foi concluída em {failed.Count} de {Scope.Count} assinatura(s)"
                      + $" ({AzureLabels.Subscription(first.Sub)}: {first.Read?.Detail ?? first.Read?.Outcome.ToString() ?? "não tentada"}).");
        }
        if (truncated > 0)
            parts.Add($"A enumeração de {AzureLabels.Family(family)} atingiu o teto da coleta em {truncated} assinatura(s).");
        return (false, string.Join(" ", parts));
    }
}

/// <summary>Rótulos legíveis do Azure (famílias, tipos de recurso, assinaturas).</summary>
public static class AzureLabels
{
    public static string Family(KnightCapability family) => family switch
    {
        KnightCapability.AzureSubscriptions => "assinaturas",
        KnightCapability.AzureAuthorization => "autorização (papéis e bloqueios)",
        KnightCapability.AzurePolicy => "atribuições de política",
        KnightCapability.AzureDefenderForCloud => "Defender para Nuvem",
        KnightCapability.AzureMonitor => "Azure Monitor",
        KnightCapability.AzureNetworking => "rede",
        KnightCapability.AzureStorage => "armazenamento",
        KnightCapability.AzureKeyVault => "Key Vault",
        KnightCapability.AzureKeyVaultCertificates => "certificados do Key Vault",
        KnightCapability.AzureCompute => "computação",
        KnightCapability.AzureAppService => "App Service",
        KnightCapability.AzureDatabases => "bancos de dados",
        KnightCapability.AzureDatabricks => "Databricks",
        KnightCapability.AzureTenantDiagnostics => "diagnóstico do Microsoft Entra ID",
        _ => family.ToString(),
    };

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [AzureView.SubscriptionType] = "assinatura",
        ["Microsoft.Storage/storageAccounts"] = "conta de armazenamento",
        ["Microsoft.KeyVault/vaults"] = "cofre de chaves",
        ["Microsoft.Network/networkSecurityGroups"] = "grupo de segurança de rede",
        ["Microsoft.Network/virtualNetworks"] = "rede virtual",
        ["Microsoft.Network/applicationGateways"] = "gateway de aplicativo",
        ["Microsoft.Network/ApplicationGatewayWebApplicationFirewallPolicies"] = "política de WAF",
        ["Microsoft.Network/networkWatchers"] = "Network Watcher",
        ["Microsoft.Network/virtualNetworkGateways"] = "gateway de rede virtual",
        ["Microsoft.Compute/virtualMachines"] = "máquina virtual",
        ["Microsoft.Compute/disks"] = "disco gerenciado",
        ["Microsoft.ContainerInstance/containerGroups"] = "grupo de contêineres",
        ["Microsoft.Batch/batchAccounts"] = "conta do Batch",
        ["Microsoft.Web/sites"] = "aplicativo do App Service",
        ["Microsoft.Web/sites/slots"] = "slot do App Service",
        ["Microsoft.Web/hostingEnvironments"] = "ambiente do App Service",
        ["Microsoft.Sql/servers"] = "servidor SQL",
        ["Microsoft.Sql/servers/databases"] = "banco SQL",
        ["Microsoft.Sql/managedInstances"] = "instância gerenciada de SQL",
        ["Microsoft.Sql/managedInstances/databases"] = "banco da instância gerenciada",
        ["Microsoft.DBforPostgreSQL/flexibleServers"] = "servidor PostgreSQL",
        ["Microsoft.DBforMySQL/flexibleServers"] = "servidor MySQL",
        ["Microsoft.DocumentDB/databaseAccounts"] = "conta do Cosmos DB",
        ["Microsoft.Cache/Redis"] = "cache do Redis",
        ["Microsoft.Cache/redisEnterprise"] = "cluster do Redis Enterprise",
        ["Microsoft.DataFactory/factories"] = "fábrica do Data Factory",
        ["Microsoft.Databricks/workspaces"] = "workspace do Databricks",
        ["Microsoft.Authorization/roleAssignments"] = "atribuição de papel",
        ["Microsoft.Authorization/roleDefinitions"] = "papel personalizado",
        ["Microsoft.Authorization/policyAssignments"] = "atribuição de política",
        ["Microsoft.Insights/activityLogAlerts"] = "alerta do log de atividades",
        ["microsoft.aadiam/diagnosticSettings"] = "configuração de diagnóstico do Entra ID",
        ["Microsoft.Network/virtualNetworks/subnets"] = "sub-rede",
        ["Microsoft.Network/networkWatchers/flowLogs"] = "log de fluxo",
        ["AEGIS/region"] = "região em uso",
        ["Microsoft.KeyVault/vaults/keys"] = "chave",
        ["Microsoft.KeyVault/vaults/secrets"] = "segredo",
        ["Microsoft.KeyVault/vaults/certificates"] = "certificado",
        ["Microsoft.Batch/batchAccounts/pools"] = "pool do Batch",
        ["Microsoft.Network/privateEndpoints"] = "endpoint privado",
        ["Microsoft.Web/serverfarms"] = "plano do App Service",
    };

    public static string Type(string type) => Types.TryGetValue(type, out var l) ? l : type;

    /// <summary>
    /// [AEGIS-KNIGHT-PRESENTATION-01] O tipo de um recurso a partir do identificador do Azure Resource Manager CONGELADO
    /// (formato oficial "/subscriptions/{id}/resourceGroups/{rg}/providers/{namespace}/{tipo}/{nome}[/{subtipo}/{nome}]").
    /// Só tipos com rótulo conhecido; qualquer outro formato devolve nulo e quem chama mantém o rótulo genérico.
    /// </summary>
    public static string? TypeOfResourceId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var s = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (s.Length < 2 || !s[0].Equals("subscriptions", StringComparison.OrdinalIgnoreCase)) return null;
        if (s.Length == 2) return Types[AzureView.SubscriptionType];
        var p = Array.FindLastIndex(s, x => x.Equals("providers", StringComparison.OrdinalIgnoreCase));
        if (p < 0)
            return s.Length == 4 && s[2].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase) ? "grupo de recursos" : null;
        var rest = s.Skip(p + 1).ToArray();
        if (rest.Length < 3 || rest.Length % 2 == 0) return null;
        var type = rest[0] + "/" + string.Join("/", rest.Skip(1).Where((_, i) => i % 2 == 0));
        return Types.TryGetValue(type, out var l) ? l : null;
    }

    public static string Subscription(AzureSubscriptionInventory s) =>
        string.IsNullOrWhiteSpace(s.DisplayName) ? s.SubscriptionId : $"{s.DisplayName} ({s.SubscriptionId})";
}

/// <summary>Uma checagem de recurso: população, veredito por item e textos da conclusão.</summary>
public sealed record AzureCheck(
    KnightCapability Family,
    string Noun,
    Func<AzureView, IEnumerable<AzureResource>> Population,
    Func<AzureResource, AzureView, KnightItemCheck> Check,
    string ExposedText,
    string PassedText,
    IReadOnlyList<KnightCapability>? AlsoRequires = null);

public static class AzureRuleKit
{
    /// <summary>Fábrica de definição de um controle do Azure.</summary>
    public static KnightIndicatorDefinition Control(
        KnightService service, string id, string title, KnightIndicatorCategory category, SeverityLevel severity,
        string recommendation, string criterion, AzureCheck check, params KnightReferenceLink[] references) =>
        KnightRuleKit.Control(KnightSourceType.MicrosoftAzure, service, id, title, category, severity, recommendation, criterion,
            c => Evaluate(c, check), references);

    /// <summary>Controle com avaliação própria (configuração do locatário, regras que cruzam recursos).</summary>
    public static KnightIndicatorDefinition Custom(
        KnightService service, string id, string title, KnightIndicatorCategory category, SeverityLevel severity,
        string recommendation, string criterion, Func<KnightEvaluationContext, AzureView, KnightControlOutcome> evaluate,
        params KnightReferenceLink[] references) =>
        KnightRuleKit.Control(KnightSourceType.MicrosoftAzure, service, id, title, category, severity, recommendation, criterion,
            c =>
            {
                var v = AzureView.Of(c);
                return v.Missing is { } m ? KnightControlOutcome.NotEvaluated(KnightRuleKit.Trim(m)) : evaluate(c, v);
            }, references);

    public static KnightControlOutcome Evaluate(KnightEvaluationContext c, AzureCheck check)
    {
        var v = AzureView.Of(c);
        if (v.Missing is { } m) return KnightControlOutcome.NotEvaluated(KnightRuleKit.Trim(m));

        var (complete, reason) = v.Completeness(check.Family);
        foreach (var extra in check.AlsoRequires ?? Array.Empty<KnightCapability>())
        {
            var (ok, why) = v.Completeness(extra);
            if (!ok) { complete = false; reason = string.Join(" ", new[] { reason, why }.Where(x => x is not null)); }
        }

        var items = check.Population(v).ToList();
        return KnightRuleKit.Population(items,
            r => check.Check(r, v),
            Describe,
            (n, t) => string.Format(CultureInfo.InvariantCulture, check.ExposedText, n, t),
            t => string.Format(CultureInfo.InvariantCulture, check.PassedText, t),
            () => complete
                ? KnightControlOutcome.NotApplicable($"nenhum(a) {check.Noun} nas {v.Scope.Count} assinatura(s) do escopo.")
                : KnightControlOutcome.NotEvaluated(KnightRuleKit.Trim(reason ?? "leitura incompleta.")),
            complete, reason);
    }

    /// <summary>Objeto de recurso (evidência por padrão; a população o promove a afetado quando não conforme).</summary>
    public static KnightIndicatorObject Describe(AzureResource r)
    {
        var where = r.Is(AzureView.SubscriptionType)
            ? $"assinatura {r.SubscriptionId}"
            : string.Join(" · ", new[] { AzureLabels.Type(r.Type), r.ResourceGroup is null ? null : $"grupo {r.ResourceGroup}" }
                .Where(x => x is not null));
        return KnightObjects.Evidence(KnightAffectedObjectKind.CloudResource, AzureResource.CompactId(r.Id),
            KnightRuleKit.Trim($"{r.Name} — {where}", 300), "");
    }

    // ---- Leituras comuns ---------------------------------------------------------------------------------

    /// <summary>Booleano com o texto encontrado; <paramref name="documentedDefault"/> é usado (e dito) quando o fato falta.</summary>
    public static KnightItemCheck Flag(AzureResource r, string path, bool expected, string label, string yes, string no,
        bool? documentedDefault = null, string? defaultSource = null)
    {
        var v = r.Bool(path);
        if (v is null && documentedDefault is { } d)
            return KnightItemCheck.Of(d == expected, $"{label}: não definido — padrão documentado: {(d ? yes : no)}{(defaultSource is null ? "" : $" ({defaultSource})")}",
                $"{label}: {(expected ? yes : no)}");
        return KnightItemCheck.Of(v is null ? null : v == expected, $"{label}: {(v is null ? "não informado" : v.Value ? yes : no)}",
            $"{label}: {(expected ? yes : no)}");
    }

    /// <summary>Texto comparado a valores aceitos (sem distinção de caixa).</summary>
    public static KnightItemCheck OneOf(AzureResource r, string path, string label, string expectedText, params string[] accepted)
    {
        var v = r.Str(path);
        return KnightItemCheck.Of(v is null ? null : accepted.Any(a => string.Equals(a, v, StringComparison.OrdinalIgnoreCase)),
            $"{label}: {v ?? "não informado"}", $"{label}: {expectedText}");
    }

    /// <summary>Acesso público de rede: "Disabled" aprova; "Enabled" reprova; ausente é desconhecido.</summary>
    public static KnightItemCheck PublicAccessDisabled(AzureResource r, string path = "properties.publicNetworkAccess")
    {
        var v = r.Str(path);
        return KnightItemCheck.Of(v is null ? null : string.Equals(v, "Disabled", StringComparison.OrdinalIgnoreCase),
            $"acesso público de rede: {PublicLabel(v)}", "acesso público de rede: desabilitado");
    }

    private static string PublicLabel(string? v) => v?.ToLowerInvariant() switch
    {
        null => "não informado",
        "disabled" => "desabilitado",
        "enabled" => "habilitado",
        "securedbyperimeter" => "controlado pelo perímetro de segurança de rede",
        _ => v,
    };

    /// <summary>Conexões de endpoint privado APROVADAS na lista de conexões do recurso.</summary>
    public static int ApprovedPrivateEndpoints(AzureResource r, string path = "properties.privateEndpointConnections") =>
        r.Items(path).Count(e =>
            string.Equals(AzureJson.Str(e, "properties.privateLinkServiceConnectionState.status"), "Approved", StringComparison.OrdinalIgnoreCase));

    public static KnightItemCheck PrivateEndpoint(AzureResource r, string path = "properties.privateEndpointConnections")
    {
        var n = ApprovedPrivateEndpoints(r, path);
        var any = r.Has(path);
        // Lista ausente na resposta: a fonte não informou (não é "sem endpoint").
        return KnightItemCheck.Of(n > 0 ? true : any ? false : null,
            n > 0 ? $"{n} conexão(ões) de endpoint privado aprovada(s)" : any ? "nenhuma conexão de endpoint privado aprovada" : "conexões de endpoint privado não informadas",
            "ao menos uma conexão de endpoint privado aprovada");
    }

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-04] Zona DNS privada dos endpoints privados APROVADOS de um recurso: cada conexão aprovada aponta
    /// para um recurso Microsoft.Network/privateEndpoints, e o grupo de zonas DNS dele precisa conter a zona documentada para o
    /// serviço (<paramref name="zone"/>). Endpoint fora das assinaturas lidas ou grupo de zonas não lido = desconhecido.
    /// </summary>
    public static KnightItemCheck PrivateDnsZone(IEnumerable<JsonElement> connections, AzureView v, string zone)
    {
        var expected = $"zona DNS privada {zone} em cada endpoint privado aprovado";
        var approved = connections.Where(e =>
            string.Equals(AzureJson.Str(e, "properties.privateLinkServiceConnectionState.status"), "Approved", StringComparison.OrdinalIgnoreCase)).ToList();
        var missing = new List<string>();
        var unknown = new List<string>();
        foreach (var conn in approved)
        {
            var peId = AzureJson.Str(conn, "properties.privateEndpoint.id");
            var name = peId?.Split('/').LastOrDefault() ?? AzureJson.Str(conn, "name") ?? "?";
            var pe = v.ById(peId);
            if (pe is null) { unknown.Add($"{name} (fora das assinaturas lidas)"); continue; }
            if (pe.Str("dns:status") is { } st) { unknown.Add($"{name} (zonas DNS não lidas: {st})"); continue; }
            var zones = pe.Items("dns:items").SelectMany(g => AzureJson.Items(g, "properties.privateDnsZoneConfigs"))
                .Select(c => AzureJson.Str(c, "properties.privateDnsZoneId")?.Split('/').LastOrDefault())
                .Where(z => z is not null).ToList();
            if (!zones.Any(z => string.Equals(z, zone, StringComparison.OrdinalIgnoreCase)))
                missing.Add(zones.Count == 0 ? $"{name} (sem grupo de zonas DNS)" : $"{name} (zonas: {string.Join(", ", zones)})");
        }
        if (missing.Count > 0) return KnightItemCheck.Of(false, "sem a zona esperada: " + KnightRuleKit.List(missing, 3), expected);
        if (unknown.Count > 0) return KnightItemCheck.Of(null, "não conferido: " + KnightRuleKit.List(unknown, 3), expected);
        return KnightItemCheck.Of(true, $"{approved.Count} endpoint(s) privado(s) com a zona {zone}", expected);
    }

    /// <summary>Identidade gerenciada (atribuída pelo sistema ou pelo usuário) declarada no recurso.</summary>
    public static KnightItemCheck ManagedIdentity(AzureResource r, bool systemAssignedOnly = false)
    {
        var t = r.Str("identity.type");
        var ok = t is not null && !string.Equals(t, "None", StringComparison.OrdinalIgnoreCase)
                 && (!systemAssignedOnly || t.Contains("SystemAssigned", StringComparison.OrdinalIgnoreCase));
        // identity ausente na resposta da ARM significa recurso sem identidade (a propriedade só vem quando existe).
        return KnightItemCheck.Of(ok, $"identidade gerenciada: {t ?? "nenhuma"}",
            systemAssignedOnly ? "identidade gerenciada atribuída pelo sistema" : "identidade gerenciada atribuída");
    }

    /// <summary>Data da ARM (ISO 8601) ou segundos Unix (atributos do Key Vault).</summary>
    public static DateTimeOffset? Date(JsonElement? v) => v switch
    {
        { ValueKind: JsonValueKind.Number } n when n.TryGetInt64(out var s) => DateTimeOffset.FromUnixTimeSeconds(s),
        { ValueKind: JsonValueKind.String } t when DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var d) => d,
        _ => null,
    };

    public static string Day(DateTimeOffset d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>A família foi lida e devolveu populações vazias: "não se aplica" só se completa (para regras próprias).</summary>
    public static KnightControlOutcome EmptyOrIncomplete(AzureView v, KnightCapability family, string noun)
    {
        var (complete, reason) = v.Completeness(family);
        return complete
            ? KnightControlOutcome.NotApplicable($"nenhum(a) {noun} nas {v.Scope.Count} assinatura(s) do escopo.")
            : KnightControlOutcome.NotEvaluated(KnightRuleKit.Trim(reason ?? "leitura incompleta."));
    }
}
