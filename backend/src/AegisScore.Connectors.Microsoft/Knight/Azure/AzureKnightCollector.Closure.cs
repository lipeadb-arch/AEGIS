using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Connectors.Microsoft.Knight.Services;

namespace AegisScore.Connectors.Microsoft.Knight.Azure;

// ============================================================================
//  [AEGIS-KNIGHT-CLOSURE-01] Leituras complementares do Azure
// ============================================================================
// Quatro leituras que dependem dos recursos já descobertos pelas famílias estáveis, cada uma com o PRÓPRIO desfecho por
// assinatura (uma falha aqui não derruba família nenhuma):
//   • VERSÃO PREVIEW — configurações de diagnóstico (assinatura e recursos) e contatos de segurança do Defender para Nuvem.
//     A resposta é conferida contra o contrato documentado da versão (lista "value", item com "properties"); qualquer
//     desvio vira limitação nomeando a versão, e o controle fica não avaliado — nunca aprovado;
//   • REFERÊNCIAS AO KEY VAULT do App Service (versão estável): só a referência e o estado de resolução. A leitura que
//     devolve os VALORES das configurações (config/list) não é usada;
//   • API DO WORKSPACE do Databricks: só quando habilitada em Integrações. Grava apenas resumos calculados aqui (contagens,
//     booleanos) — nunca a configuração Spark, o conteúdo de scripts ou um token.

public sealed partial class AzureKnightCollector
{
    /// <summary>Resposta que não segue o contrato documentado da versão lida (preview ou não).</summary>
    private sealed class ContractException : Exception
    {
        public ContractException(string message) : base(message) { }
    }

    private const string ContractPrefix = "Resposta fora do contrato documentado";

    /// <summary>
    /// Lista paginada com o contrato conferido: cada página é um objeto com o array <c>value</c>, e cada item satisfaz
    /// <paramref name="valid"/>. O próximo link precisa ficar no mesmo host.
    /// </summary>
    private async Task<List<JsonElement>> ReadContractListAsync(string token, string url, Func<JsonElement, bool> valid, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        var host = new Uri(url).Host;
        string? next = url;
        for (var page = 0; next is not null; page++)
        {
            if (page >= 50) throw new ContractException("paginação acima de 50 páginas");
            var root = await _rest.GetAsync(token, next, ct);
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
                throw new ContractException("a resposta não traz a lista \"value\"");
            foreach (var item in value.EnumerateArray())
            {
                if (!valid(item)) throw new ContractException("item da lista sem os campos documentados");
                items.Add(item.Clone());
            }
            next = KnightJson.Str(root, "nextLink");
            if (next is not null && (!Uri.TryCreate(next, UriKind.Absolute, out var nu) || !string.Equals(nu.Host, host, StringComparison.OrdinalIgnoreCase)))
                throw new ContractException("próximo link fora do host de origem");
        }
        return items;
    }

    private static bool WithProperties(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object && KnightJson.Prop(item, "properties").ValueKind == JsonValueKind.Object;

    /// <summary>Configuração de diagnóstico conforme o contrato: <c>properties</c> e, quando há, <c>logs</c> como lista.</summary>
    private static bool DiagnosticItem(JsonElement item) =>
        WithProperties(item) && KnightJson.Prop(KnightJson.Prop(item, "properties"), "logs").ValueKind is JsonValueKind.Array or JsonValueKind.Undefined or JsonValueKind.Null;

    /// <summary>Contato de segurança conforme o contrato: <c>properties</c>; origens de notificação, quando há, como lista.</summary>
    private static bool SecurityContactItem(JsonElement item) =>
        WithProperties(item) && KnightJson.Prop(KnightJson.Prop(item, "properties"), "notificationsSources").ValueKind
            is JsonValueKind.Array or JsonValueKind.Undefined or JsonValueKind.Null;

    private void FailRead(FamilyOutcome outcome, Exception ex, string what)
    {
        switch (ex)
        {
            case EntraGraphException g:
                var (o, reason) = KnightCapabilityRecorder.Classify(g, Label);
                outcome.Fail(o, KnightRuleText.Trim($"{what}: {reason}", 480));
                break;
            case ContractException c:
                outcome.Fail(KnightCapabilityOutcome.Error, KnightRuleText.Trim($"{what}: {ContractPrefix} ({c.Message}).", 480));
                break;
            default:
                outcome.Fail(KnightCapabilityOutcome.Unavailable, $"{what}: falha de rede ou tempo esgotado.");
                break;
        }
    }

    private static bool Recoverable(Exception ex) =>
        ex is EntraGraphException or ContractException or System.Net.Http.HttpRequestException or TaskCanceledException or TimeoutException;

    // ---- Assinatura: exportação do log de atividades e contatos de segurança (preview) ------------------------------

    private async Task ReadSubscriptionPreviewListsAsync(string token, string sub, List<Building> resources,
        Dictionary<KnightCapability, FamilyOutcome> outcomes, CancellationToken ct)
    {
        var diagApi = AzureArmPlan.DiagnosticSettingsPreviewApi;
        try
        {
            var settings = await ReadContractListAsync(token,
                Url($"{AzureArmPlan.ArmHost}/subscriptions/{sub}/providers/Microsoft.Insights/diagnosticSettings", diagApi), DiagnosticItem, ct);
            foreach (var item in settings)
            {
                var b = Start(item, sub, KnightCapability.AzureActivityLogExport, $"/subscriptions/{sub}");
                b.Type = SubscriptionDiagnosticType;
                Extract(item, AzureArmPlan.DiagnosticKeep.Where(k => k != "name"), "", b.Facts);
                resources.Add(b);
            }
        }
        catch (Exception ex) when (Recoverable(ex) && !ct.IsCancellationRequested)
        {
            FailRead(outcomes[KnightCapability.AzureActivityLogExport], ex, $"configurações de diagnóstico da assinatura (versão {diagApi})");
        }

        var contactsApi = AzureArmPlan.SecurityContactsPreviewApi;
        try
        {
            var contacts = await ReadContractListAsync(token,
                Url($"{AzureArmPlan.ArmHost}/subscriptions/{sub}/providers/Microsoft.Security/securityContacts", contactsApi), SecurityContactItem, ct);
            foreach (var item in contacts)
            {
                var b = Start(item, sub, KnightCapability.AzureSecurityContacts, $"/subscriptions/{sub}");
                b.Type = SecurityContactType;
                // Endereços e telefone NÃO são gravados: só quantos endereços existem.
                Extract(item, new[] { "properties.isEnabled", "properties.notificationsByRole", "properties.notificationsSources" }, "", b.Facts);
                var emails = (KnightJson.Str(KnightJson.Prop(item, "properties"), "emails") ?? "")
                    .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                b.Facts["emails:count"] = JsonSerializer.SerializeToElement(emails.Length);
                resources.Add(b);
            }
        }
        catch (Exception ex) when (Recoverable(ex) && !ct.IsCancellationRequested)
        {
            FailRead(outcomes[KnightCapability.AzureSecurityContacts], ex, $"contatos de segurança do Defender para Nuvem (versão {contactsApi})");
        }
    }

    private const string SubscriptionDiagnosticType = AegisScore.Application.Knight.Catalog.AzureResourceLogs.SubscriptionDiagnosticType;
    private const string SecurityContactType = AegisScore.Application.Knight.Catalog.AzureResourceLogs.SecurityContactType;

    // ---- Recursos: configurações de diagnóstico (preview) ----------------------------------------------------------

    private async Task ReadResourceDiagnosticsAsync(string token, List<Building> resources,
        Dictionary<string, Dictionary<KnightCapability, FamilyOutcome>> outcomes, CancellationToken ct)
    {
        var api = AzureArmPlan.DiagnosticSettingsPreviewApi;
        var reads = 0;
        // Uma recusa ou mudança de contrato numa assinatura vale para a versão inteira: as demais leituras da mesma
        // assinatura não são tentadas (o recurso fica "não lido"), em vez de repetir a mesma falha recurso a recurso.
        var broken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in resources.Where(r => AzureArmPlan.ResourceLogTypes.ContainsKey(r.Type)).ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (!outcomes.TryGetValue(r.SubscriptionId, out var fam)) continue;
            var outcome = fam[KnightCapability.AzureResourceDiagnostics];
            if (broken.Contains(r.SubscriptionId)) { r.Facts["diag:status"] = JsonSerializer.SerializeToElement("NotRead"); continue; }
            if (++reads > AzureArmPlan.MaxResourceDiagnosticReads)
            {
                outcome.Truncated = true;
                r.Facts["diag:status"] = JsonSerializer.SerializeToElement("NotRead");
                continue;
            }

            var suffix = AzureArmPlan.ResourceLogTypes[r.Type];
            try
            {
                var settings = await ReadContractListAsync(token,
                    Url($"{AzureArmPlan.ArmHost}{r.Id}{suffix}/providers/Microsoft.Insights/diagnosticSettings", api), DiagnosticItem, ct);
                var list = new JsonArray();
                foreach (var s in settings) list.Add(Reduce(s, AzureArmPlan.DiagnosticKeep));
                r.Facts["diag:items"] = JsonSerializer.SerializeToElement(list);
            }
            catch (EntraGraphException ex) when (ex.HttpStatusCode == 404)
            {
                // O recurso deixou de existir entre a listagem e esta leitura, ou o serviço não respondeu pela operação.
                r.Facts["diag:status"] = JsonSerializer.SerializeToElement("NotFound");
                outcome.ChildFailures++;
            }
            catch (Exception ex) when (Recoverable(ex) && !ct.IsCancellationRequested)
            {
                r.Facts["diag:status"] = JsonSerializer.SerializeToElement(ex is ContractException ? "ContractChanged"
                    : ex is EntraGraphException g ? g.Kind.ToString() : "Unavailable");
                FailRead(outcome, ex, $"configurações de diagnóstico de {r.Name} (versão {api})");
                broken.Add(r.SubscriptionId);
            }
        }
    }

    // ---- App Service: referências ao Key Vault (estável) ------------------------------------------------------------

    private async Task ReadKeyVaultReferencesAsync(string token, List<Building> resources,
        Dictionary<string, Dictionary<KnightCapability, FamilyOutcome>> outcomes, CancellationToken ct)
    {
        var broken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in resources.Where(r => r.Type.Equals("Microsoft.Web/sites", StringComparison.OrdinalIgnoreCase)
                                               || r.Type.Equals("Microsoft.Web/sites/slots", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (!outcomes.TryGetValue(r.SubscriptionId, out var fam)) continue;
            var outcome = fam[KnightCapability.AzureAppSettingsKeyVaultReferences];
            if (broken.Contains(r.SubscriptionId)) { r.Facts["kvrefs:status"] = JsonSerializer.SerializeToElement("NotRead"); continue; }
            try
            {
                var refs = await ReadContractListAsync(token,
                    Url(AzureArmPlan.ArmHost + r.Id + AzureArmPlan.KeyVaultReferencesPath, AzureArmPlan.AppServiceApi), WithProperties, ct);
                var list = new JsonArray();
                foreach (var item in refs)
                {
                    var reduced = Reduce(item, AzureArmPlan.KeyVaultReferenceKeep);
                    // O nome da configuração é o último segmento do id quando o item não traz "name".
                    if (reduced["name"] is null && KnightJson.Str(item, "id") is { } id) reduced["name"] = id.TrimEnd('/').Split('/').Last();
                    list.Add(reduced);
                }
                r.Facts["kvrefs:items"] = JsonSerializer.SerializeToElement(list);
            }
            catch (EntraGraphException ex) when (ex.HttpStatusCode == 404)
            {
                r.Facts["kvrefs:status"] = JsonSerializer.SerializeToElement("NotFound");
                outcome.ChildFailures++;
            }
            catch (Exception ex) when (Recoverable(ex) && !ct.IsCancellationRequested)
            {
                r.Facts["kvrefs:status"] = JsonSerializer.SerializeToElement(ex is ContractException ? "ContractChanged"
                    : ex is EntraGraphException g ? g.Kind.ToString() : "Unavailable");
                FailRead(outcome, ex, $"referências ao Key Vault de {r.Name}");
                broken.Add(r.SubscriptionId);
            }
        }
    }

    // ---- Databricks: API do próprio workspace (habilitada em Integrações) -------------------------------------------

    internal const string DatabricksDisabledReason =
        "A leitura da API dos workspaces do Databricks não está habilitada em Configurações → Integrações. Ela exige que a "
        + "aplicação seja adicionada a cada workspace como entidade de serviço administradora — acesso concedido dentro do Databricks.";

    internal const int MaxScimPages = 50;

    private async Task ReadDatabricksWorkspacesAsync(IMicrosoftGraphCredentials cfg, KnightMicrosoftServiceConfiguration? config,
        List<Building> resources, Dictionary<string, Dictionary<KnightCapability, FamilyOutcome>> outcomes, CancellationToken ct)
    {
        const KnightCapability family = KnightCapability.AzureDatabricksWorkspaceApi;
        var workspaces = resources.Where(r => r.Type.Equals("Microsoft.Databricks/workspaces", StringComparison.OrdinalIgnoreCase)
                                              && outcomes.ContainsKey(r.SubscriptionId)).ToList();
        if (workspaces.Count == 0) return; // nada a ler: a família fica concluída sem item
        if (config?.DatabricksWorkspaceApi != true)
        {
            // Só as assinaturas COM workspace ficam sem a leitura; nelas os controles da API ficam não avaliados.
            foreach (var w in workspaces) outcomes[w.SubscriptionId][family].Fail(KnightCapabilityOutcome.NotAttempted, DatabricksDisabledReason);
            return;
        }

        string token;
        try
        {
            token = await _tokens.AcquireAsync(cfg, AzureArmPlan.DatabricksScope, ct);
        }
        catch (EntraGraphException ex)
        {
            var (o, reason) = KnightCapabilityRecorder.Classify(ex, Label);
            foreach (var w in workspaces) outcomes[w.SubscriptionId][family].Fail(o, KnightRuleText.Trim("Token do Azure Databricks: " + reason, 480));
            return;
        }

        foreach (var w in workspaces)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = outcomes[w.SubscriptionId][family];
            var host = w.Facts.TryGetValue("properties.workspaceUrl", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(host) || !host.EndsWith(AzureArmPlan.DatabricksHostSuffix, StringComparison.OrdinalIgnoreCase)
                || host.Contains('/') || host.Contains(':'))
            {
                w.Facts["dbx:status"] = JsonSerializer.SerializeToElement("NoWorkspaceUrl");
                outcome.Fail(KnightCapabilityOutcome.Error, $"workspace {w.Name}: o Resource Manager não informou um endereço de workspace oficial (*{AzureArmPlan.DatabricksHostSuffix}).");
                continue;
            }
            var baseUrl = "https://" + host;

            await DbxAsync(w, outcome, "metastore", token, baseUrl + "/api/2.1/unity-catalog/current-metastore-assignment", ct,
                el => new JsonObject { ["assigned"] = !string.IsNullOrWhiteSpace(KnightJson.Str(el, "metastore_id")) },
                notFound: ex => string.Equals(ex.GraphErrorCode, "METASTORE_DOES_NOT_EXIST", StringComparison.OrdinalIgnoreCase)
                    ? new JsonObject { ["assigned"] = false } : null);

            await DbxAsync(w, outcome, "conf", token, baseUrl + "/api/2.0/workspace-conf?keys=enableTokensConfig,maxTokenLifetimeDays", ct,
                el => new JsonObject
                {
                    ["enableTokensConfig"] = KnightJson.Str(el, "enableTokensConfig"),
                    ["maxTokenLifetimeDays"] = KnightJson.Str(el, "maxTokenLifetimeDays"),
                });

            await DbxAsync(w, outcome, "tokenPerms", token, baseUrl + "/api/2.0/permissions/authorization/tokens", ct, TokenPermissions);

            await ScimAsync(w, outcome, "scimUsers", token, baseUrl + "/api/2.0/preview/scim/v2/Users?attributes=userName,externalId", "userName", ct);
            await ScimAsync(w, outcome, "scimGroups", token, baseUrl + "/api/2.0/preview/scim/v2/Groups?attributes=displayName,externalId", "displayName", ct);

            await ClustersAsync(w, outcome, token, baseUrl, ct);

            await DbxAsync(w, outcome, "globalInit", token, baseUrl + "/api/2.0/global-init-scripts", ct,
                el => new JsonObject
                {
                    // Só metadados (nome e se está ligado): o conteúdo dos scripts não é lido.
                    ["enabled"] = KnightJson.Prop(el, "scripts").ValueKind == JsonValueKind.Array
                        ? KnightJson.Prop(el, "scripts").EnumerateArray().Count(s => KnightJson.Bool(s, "enabled") == true) : 0,
                });
        }
    }

    /// <summary>Uma leitura do workspace: resumo em <c>dbx:{chave}</c>; falha em <c>dbx:{chave}:status</c> e na capacidade.</summary>
    private async Task DbxAsync(Building w, FamilyOutcome outcome, string key, string token, string url, CancellationToken ct,
        Func<JsonElement, JsonObject> summarize, Func<EntraGraphException, JsonObject?>? notFound = null)
    {
        try
        {
            var root = await _rest.GetAsync(token, url, ct);
            if (root.ValueKind != JsonValueKind.Object) throw new ContractException("a resposta não é um objeto JSON");
            w.Facts["dbx:" + key] = JsonSerializer.SerializeToElement(summarize(root));
        }
        catch (EntraGraphException ex) when (ex.HttpStatusCode == 404 && notFound?.Invoke(ex) is { } fact)
        {
            w.Facts["dbx:" + key] = JsonSerializer.SerializeToElement(fact);
        }
        catch (Exception ex) when (Recoverable(ex) && !ct.IsCancellationRequested)
        {
            w.Facts["dbx:" + key + ":status"] = JsonSerializer.SerializeToElement(ex is ContractException ? "ContractChanged"
                : ex is EntraGraphException g ? (g.HttpStatusCode == 404 ? "NotFound" : g.Kind.ToString()) : "Unavailable");
            FailRead(outcome, ex, $"workspace {w.Name} ({DbxLabel(key)})");
        }
    }

    private static string DbxLabel(string key) => key switch
    {
        "metastore" => "metastore do Unity Catalog",
        "conf" => "configuração de tokens do workspace",
        "tokenPerms" => "permissões de uso de tokens",
        "scimUsers" => "usuários (SCIM)",
        "scimGroups" => "grupos (SCIM)",
        "clusters" => "clusters",
        "globalInit" => "scripts de inicialização globais",
        _ => key,
    };

    /// <summary>
    /// Permissões de uso de tokens pessoais: quais GRUPOS podem usar (CAN_USE/CAN_MANAGE) e se o grupo de todos os usuários
    /// do workspace ("users") está entre eles. Usuários e entidades de serviço são só contados.
    /// </summary>
    internal static JsonObject TokenPermissions(JsonElement root)
    {
        var groups = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var principals = 0;
        if (KnightJson.Prop(root, "access_control_list") is { ValueKind: JsonValueKind.Array } acl)
        {
            foreach (var entry in acl.EnumerateArray())
            {
                var levels = KnightJson.Prop(entry, "all_permissions") is { ValueKind: JsonValueKind.Array } p
                    ? p.EnumerateArray().Select(x => KnightJson.Str(x, "permission_level")).ToList()
                    : new List<string?>();
                if (!levels.Any(l => l is "CAN_USE" or "CAN_MANAGE")) continue;
                if (KnightJson.Str(entry, "group_name") is { } g) groups.Add(g);
                else principals++;
            }
        }
        else throw new ContractException("a resposta não traz \"access_control_list\"");
        return new JsonObject
        {
            ["usersGroupCanUse"] = groups.Contains("users"),
            ["groups"] = new JsonArray(groups.Select(g => (JsonNode?)JsonValue.Create(g)).ToArray()),
            ["otherPrincipals"] = principals,
        };
    }

    /// <summary>
    /// Usuários ou grupos do workspace pela API SCIM, paginada por índice: quantos existem e quantos têm vínculo externo
    /// (<c>externalId</c>, preenchido pelo provisionamento a partir do Entra ID). Até 50 nomes SEM vínculo ficam como evidência.
    /// </summary>
    private async Task ScimAsync(Building w, FamilyOutcome outcome, string key, string token, string url, string nameAttr, CancellationToken ct)
    {
        try
        {
            int total = 0, external = 0, seen = 0;
            var local = new List<string>();
            var truncated = false;
            for (var page = 0; ; page++)
            {
                if (page >= MaxScimPages) { truncated = true; break; }
                var root = await _rest.GetAsync(token, $"{url}&startIndex={seen + 1}&count=100", ct);
                if (root.ValueKind != JsonValueKind.Object) throw new ContractException("a resposta SCIM não é um objeto");
                total = (int)(KnightJson.Prop(root, "totalResults") is { ValueKind: JsonValueKind.Number } t && t.TryGetInt32(out var n) ? n : 0);
                var res = KnightJson.Prop(root, "Resources");
                if (res.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) break;
                if (res.ValueKind != JsonValueKind.Array) throw new ContractException("\"Resources\" não é uma lista");
                var count = 0;
                foreach (var item in res.EnumerateArray())
                {
                    count++;
                    if (!string.IsNullOrWhiteSpace(KnightJson.Str(item, "externalId"))) external++;
                    else if (local.Count < 50 && KnightJson.Str(item, nameAttr) is { } nm) local.Add(nm);
                }
                seen += count;
                if (count == 0 || seen >= total) break;
            }
            w.Facts["dbx:" + key] = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["total"] = Math.Max(total, seen),
                ["read"] = seen,
                ["external"] = external,
                ["truncated"] = truncated,
                ["localNames"] = new JsonArray(local.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            });
        }
        catch (Exception ex) when (Recoverable(ex) && !ct.IsCancellationRequested)
        {
            w.Facts["dbx:" + key + ":status"] = JsonSerializer.SerializeToElement(ex is ContractException ? "ContractChanged"
                : ex is EntraGraphException g ? g.Kind.ToString() : "Unavailable");
            FailRead(outcome, ex, $"workspace {w.Name} ({DbxLabel(key)})");
        }
    }

    /// <summary>
    /// Clusters do workspace: para cada um, só se a configuração Spark liga a criptografia de rede
    /// (<c>spark.network.crypto.enabled</c>) e quantos scripts de inicialização ele declara. Nenhum valor de configuração,
    /// variável de ambiente ou script é gravado.
    /// </summary>
    private async Task ClustersAsync(Building w, FamilyOutcome outcome, string token, string baseUrl, CancellationToken ct)
    {
        try
        {
            var list = new JsonArray();
            string? pageToken = null;
            for (var page = 0; page < 20; page++)
            {
                var url = baseUrl + "/api/2.1/clusters/list?page_size=100" + (pageToken is null ? "" : "&page_token=" + Uri.EscapeDataString(pageToken));
                var root = await _rest.GetAsync(token, url, ct);
                if (root.ValueKind != JsonValueKind.Object) throw new ContractException("a resposta não é um objeto JSON");
                var clusters = KnightJson.Prop(root, "clusters");
                if (clusters.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in clusters.EnumerateArray())
                    {
                        var conf = KnightJson.Prop(c, "spark_conf");
                        var crypto = conf.ValueKind == JsonValueKind.Object
                                     && string.Equals(KnightJson.Str(conf, "spark.network.crypto.enabled"), "true", StringComparison.OrdinalIgnoreCase);
                        var init = KnightJson.Prop(c, "init_scripts") is { ValueKind: JsonValueKind.Array } s ? s.GetArrayLength() : 0;
                        list.Add(new JsonObject
                        {
                            ["name"] = KnightJson.Str(c, "cluster_name"),
                            ["source"] = KnightJson.Str(c, "cluster_source"),
                            ["networkCrypto"] = crypto,
                            ["initScripts"] = init,
                        });
                    }
                }
                else if (clusters.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                    throw new ContractException("\"clusters\" não é uma lista");
                pageToken = KnightJson.Str(root, "next_page_token");
                if (string.IsNullOrEmpty(pageToken)) break;
            }
            w.Facts["dbx:clusters"] = JsonSerializer.SerializeToElement(list);
        }
        catch (Exception ex) when (Recoverable(ex) && !ct.IsCancellationRequested)
        {
            w.Facts["dbx:clusters:status"] = JsonSerializer.SerializeToElement(ex is ContractException ? "ContractChanged"
                : ex is EntraGraphException g ? g.Kind.ToString() : "Unavailable");
            FailRead(outcome, ex, $"workspace {w.Name} (clusters)");
        }
    }
}
