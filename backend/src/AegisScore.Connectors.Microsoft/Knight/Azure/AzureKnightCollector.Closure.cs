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
// Leituras que dependem dos recursos já descobertos pelas famílias estáveis, cada uma com o PRÓPRIO desfecho por
// assinatura (uma falha aqui não derruba família nenhuma):
//   • VERSÃO PREVIEW — configurações de diagnóstico (assinatura e recursos) e contatos de segurança do Defender para Nuvem.
//     A resposta é conferida contra o contrato documentado da versão (lista "value", item com "properties"); qualquer
//     desvio vira limitação nomeando a versão, e o controle fica não avaliado — nunca aprovado;
//   • REFERÊNCIAS AO KEY VAULT do App Service (versão estável): só a referência e o estado de resolução. A leitura que
//     devolve os VALORES das configurações (config/list) não é usada;
//   • API DO WORKSPACE do Databricks: [AEGIS-KNIGHT-READONLY-01] retirada — não há identidade somente leitura documentada
//     para ela; a capacidade é registrada como não tentada, com o motivo.

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

    // ---- Databricks: API do próprio workspace (retirada no AEGIS-KNIGHT-READONLY-01) -----------------------------------
    // Nenhuma leitura da API do workspace (Unity Catalog, configuração e permissões de tokens, SCIM, clusters, scripts
    // globais) tem identidade somente leitura documentada: a listagem de permissões de tokens e os scripts globais são
    // exclusivos do administrador do workspace, e todo membro do workspace recebe ao menos um direito de acesso — os
    // documentados ("Workspace access", "Databricks SQL access") leem e ESCREVEM objetos. O workspace não é mais tocado:
    // a capacidade fica não tentada, com o motivo, nas assinaturas que têm workspace. O Unity Catalog passa a ser lido
    // pelo Resource Manager (propriedade isUcEnabled, papel Leitor).

    internal const string DatabricksNotRequestedReason =
        "Não solicitada: a API dos workspaces do Databricks não tem acesso somente leitura documentado — listar permissões de tokens "
        + "e scripts de inicialização globais é exclusivo do administrador do workspace, e os direitos de acesso de um membro comum "
        + "também permitem escrever objetos. O AEGIS opera só com leitura; os controles que dependem dela ficam não avaliados.";

    private static void MarkDatabricksWorkspaceApiNotRequested(List<Building> resources,
        Dictionary<string, Dictionary<KnightCapability, FamilyOutcome>> outcomes)
    {
        // Só as assinaturas COM workspace ficam sem a leitura; sem workspace a família fica concluída sem item.
        foreach (var w in resources.Where(r => r.Type.Equals("Microsoft.Databricks/workspaces", StringComparison.OrdinalIgnoreCase)
                                               && outcomes.ContainsKey(r.SubscriptionId)))
            outcomes[w.SubscriptionId][KnightCapability.AzureDatabricksWorkspaceApi].Fail(KnightCapabilityOutcome.NotAttempted, DatabricksNotRequestedReason);
    }
}
