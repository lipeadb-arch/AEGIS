using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Connectors.Microsoft.Knight.Services;
using AegisScore.Domain;
using Microsoft.Extensions.Logging;

namespace AegisScore.Connectors.Microsoft.Knight.Azure;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-04] Coletor do Microsoft Azure pelo Resource Manager, somente leitura (papel Leitor do Azure RBAC).
///
/// <para>Percurso: descobre as assinaturas visíveis à aplicação, aplica o escopo pedido (lista explícita, ou todas), e lê,
/// assinatura a assinatura, cada família do <see cref="AzureArmPlan"/>. Cada família tem o próprio desfecho POR
/// assinatura — gravado no inventário da assinatura — e a falha numa não invalida as outras. No fim, leituras de escopo do
/// locatário (diagnóstico do Entra ID, política de assinaturas) e as complementares: certificados dos cofres pelo plano de
/// dados (exige papel no cofre) e o estado das contas de usuário com papel no Azure (Microsoft Graph).</para>
///
/// <para>Nada além dos caminhos da tabela é gravado. Um recurso lido parcialmente (leitura filha recusada) carrega o
/// desfecho da filha como fato — a regra trata o valor como desconhecido, nunca como aprovado.</para>
/// </summary>
public sealed partial class AzureKnightCollector : KnightRestCollectorBase
{
    internal const int MaxGraphUsers = 500;
    internal const int MaxCertificatesPerVault = 200;

    private readonly IMicrosoftAppTokenClient _tokens;
    private readonly IMicrosoftRestClient _rest;
    private readonly IEntraGraphClient _graph;

    public AzureKnightCollector(IMicrosoftAppTokenClient tokens, IMicrosoftRestClient rest, IEntraGraphClient graph,
        ILogger<AzureKnightCollector>? log = null, TimeProvider? time = null) : base(log, time)
    {
        _tokens = tokens;
        _rest = rest;
        _graph = graph;
    }

    public override KnightSourceType Source => KnightSourceType.MicrosoftAzure;

    /// <summary>Famílias na ordem de execução (a descoberta primeiro).</summary>
    internal static readonly KnightCapability[] Families =
    {
        KnightCapability.AzureSubscriptions,
        KnightCapability.AzureAuthorization,
        KnightCapability.AzurePolicy,
        KnightCapability.AzureDefenderForCloud,
        KnightCapability.AzureMonitor,
        KnightCapability.AzureNetworking,
        KnightCapability.AzureStorage,
        KnightCapability.AzureKeyVault,
        KnightCapability.AzureKeyVaultCertificates,
        KnightCapability.AzureCompute,
        KnightCapability.AzureAppService,
        KnightCapability.AzureDatabases,
        KnightCapability.AzureDatabricks,
        KnightCapability.AzureTenantDiagnostics,
        // [AEGIS-KNIGHT-CLOSURE-01] Lidas depois das famílias estáveis (dependem dos recursos já descobertos).
        KnightCapability.AzureActivityLogExport,
        KnightCapability.AzureSecurityContacts,
        KnightCapability.AzureResourceDiagnostics,
        KnightCapability.AzureAppSettingsKeyVaultReferences,
        KnightCapability.AzureDatabricksWorkspaceApi,
    };

    public override IReadOnlyList<KnightCapability> Capabilities => Families;

    /// <summary>Recurso em montagem (os fatos ainda podem receber leituras complementares antes de virar documento).</summary>
    private sealed class Building
    {
        public required string Id;
        public required string Type;
        public required string Name;
        public required string SubscriptionId;
        public string? ResourceGroup;
        public string? Location;
        public string? Kind;
        public KnightCapability Family;
        public string? ParentId;
        public readonly Dictionary<string, JsonElement> Facts = new(StringComparer.Ordinal);
    }

    /// <summary>Desfecho de uma família numa assinatura (o primeiro erro prevalece).</summary>
    private sealed class FamilyOutcome
    {
        public KnightCapabilityOutcome Outcome = KnightCapabilityOutcome.Collected;
        public bool Truncated;
        public string? Detail;
        public int ChildFailures;
        public KnightCapabilityDiagnostic? Diagnostic;

        public void Fail(KnightCapabilityOutcome outcome, string detail, KnightCapabilityDiagnostic? diagnostic = null)
        {
            if (Outcome != KnightCapabilityOutcome.Collected) return;
            Outcome = outcome;
            Detail = detail;
            Diagnostic = diagnostic;
        }
    }

    protected override async Task CollectCoreAsync(
        IMicrosoftGraphCredentials cfg, KnightSourceConfiguration configuration, KnightCapabilityRecorder recorder, CancellationToken ct)
    {
        var scope = (configuration as KnightMicrosoftServiceConfiguration)?.SubscriptionScope ?? Array.Empty<string>();
        var token = await _tokens.AcquireAsync(cfg, AzureArmPlan.ArmScope, ct);

        // ---- 1) Descoberta ---------------------------------------------------------------------------------
        List<(string Id, string? Name, string? State)> visible;
        try
        {
            visible = new();
            await foreach (var s in _rest.GetPagedAsync(token, $"{AzureArmPlan.ArmHost}/subscriptions?api-version={AzureArmPlan.SubscriptionsApi}", ct))
            {
                var id = KnightJson.Str(s, "subscriptionId");
                if (id is not null) visible.Add((id, KnightJson.Str(s, "displayName"), KnightJson.Str(s, "state")));
            }
        }
        catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, ct))
        {
            // [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Qualquer falha da descoberta (HTTP, tempo, circuito, transporte, contrato) é
            // classificada; antes só a falha HTTP era reconhecida e as demais derrubavam a sincronização inteira.
            var failure = KnightFailureClassifier.Classify(ex, Label, "discovery", null, recorder.CorrelationId);
            foreach (var f in Families)
            {
                if (f == KnightCapability.AzureSubscriptions) recorder.Record(f, failure);
                else recorder.Record(f, KnightCapabilityOutcome.NotAttempted, "A descoberta das assinaturas falhou; nenhuma família foi lida.");
            }
            return;
        }

        var requested = scope.Select(s => s.ToLowerInvariant()).ToHashSet();
        var inScope = visible
            .Where(s => requested.Count == 0 ? !string.Equals(s.State, "Disabled", StringComparison.OrdinalIgnoreCase) : requested.Contains(s.Id.ToLowerInvariant()))
            .ToList();
        var notVisible = requested.Where(r => visible.All(v => !string.Equals(v.Id, r, StringComparison.OrdinalIgnoreCase))).ToList();

        var resources = new List<Building>();
        var outcomes = inScope.ToDictionary(s => s.Id, _ => Families.ToDictionary(f => f, _ => new FamilyOutcome()), StringComparer.OrdinalIgnoreCase);

        // ---- 2) Famílias, assinatura a assinatura -----------------------------------------------------------
        foreach (var sub in inScope)
        {
            foreach (var read in AzureArmPlan.Reads)
            {
                ct.ThrowIfCancellationRequested();
                var outcome = outcomes[sub.Id][read.Family];
                try
                {
                    await ReadFamilyAsync(token, sub.Id, read, resources, outcome, ct);
                }
                catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, ct))
                {
                    // [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] A falha fica NESTA família desta assinatura; as demais assinaturas e
                    // famílias seguem, e o que já foi lido nelas é preservado.
                    var failure = KnightFailureClassifier.Classify(ex, Label, "read", null, recorder.CorrelationId);
                    outcome.Fail(failure.Outcome, $"{read.Label}: {failure.Reason}", failure.Diagnostic);
                }
            }
        }

        // ---- 3) Certificados dos cofres (plano de dados) ----------------------------------------------------
        await ReadVaultCertificatesAsync(cfg, resources, outcomes, ct);

        // ---- 3b) [AEGIS-KNIGHT-CLOSURE-01] Versões preview e referências ao Key Vault ---------------------------
        foreach (var sub in inScope)
            await ReadSubscriptionPreviewListsAsync(token, sub.Id, resources, outcomes[sub.Id], ct);
        await ReadResourceDiagnosticsAsync(token, resources, outcomes, ct);
        await ReadKeyVaultReferencesAsync(token, resources, outcomes, ct);
        // [AEGIS-KNIGHT-READONLY-01] A API do workspace do Databricks não é lida (sem acesso somente leitura documentado).
        MarkDatabricksWorkspaceApiNotRequested(resources, outcomes);

        // ---- 4) Escopo do locatário e complementares --------------------------------------------------------
        var tenantDiag = await ReadTenantListAsync(token, AzureArmPlan.EntraDiagnostics, resources, ct);
        var stacks = await ReadRuntimeStacksAsync(token, resources, ct);
        var policy = await ReadSubscriptionPolicyAsync(token, resources, ct);
        var users = await ReadRoleAssignmentUsersAsync(cfg, resources, ct);

        // ---- 5) Documentos e desfechos ----------------------------------------------------------------------
        // Atribuições herdadas de cima (grupo de gerenciamento, raiz) aparecem na lista de CADA assinatura: o mesmo id é
        // gravado uma vez só.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in resources)
        {
            if (!seen.Add(r.Id)) continue;
            var doc = new AzureResource(r.Id, r.Type, r.Name, r.SubscriptionId, r.ResourceGroup, r.Location, r.Kind, r.Family, r.Facts, r.ParentId);
            recorder.Documents.Add(KnightTenantConfiguration.Document(AzureResource.CompactId(r.Id), r.Name, doc));
        }
        foreach (var s in visible)
        {
            var isIn = outcomes.TryGetValue(s.Id, out var fam);
            var reads = isIn
                ? fam!.Where(kv => kv.Key != KnightCapability.AzureSubscriptions && kv.Key != KnightCapability.AzureTenantDiagnostics)
                    .Select(kv => new AzureFamilyRead(kv.Key, kv.Value.Outcome, kv.Value.Truncated,
                        kv.Value.ChildFailures > 0 && kv.Value.Outcome == KnightCapabilityOutcome.Collected
                            ? $"{kv.Value.ChildFailures} leitura(s) filha(s) recusada(s) ou indisponível(is)."
                            : kv.Value.Detail))
                    .ToList()
                : new List<AzureFamilyRead>();
            recorder.Documents.Add(KnightTenantConfiguration.Document(s.Id, s.Name,
                new AzureSubscriptionInventory(s.Id, s.Name, s.State, isIn, reads)));
        }

        // [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Assinatura PEDIDA e não visível entra no inventário como parte do escopo, com
        // todas as famílias não lidas. Antes ela simplesmente sumia: o escopo avaliado encolhia sem aviso e um controle podia
        // ser aprovado "em todas as assinaturas" sem a que faltava. Agora a regra a vê como não lida e não aprova o escopo.
        const string notVisibleReason =
            "Assinatura pedida no escopo e não visível à aplicação nesta descoberta: nenhuma família foi consultada nela. Não "
            + "prova que falte o papel Leitor (a assinatura pode não existir neste locatário, estar em outro diretório ou ter "
            + "sido digitada errada) e não prova ausência de recursos.";
        foreach (var id in notVisible)
        {
            var reads = Families
                .Where(f => f is not (KnightCapability.AzureSubscriptions or KnightCapability.AzureTenantDiagnostics))
                .Select(f => new AzureFamilyRead(f, KnightCapabilityOutcome.NotAttempted, false, notVisibleReason))
                .ToList();
            recorder.Documents.Add(KnightTenantConfiguration.Document(id, null,
                new AzureSubscriptionInventory(id, null, null, true, reads, Visible: false)));
        }

        // A descoberta CONCLUIU (a lista foi lida). O que ela encontrou é dito com o que prova e o que não prova.
        var discoveryDetail = inScope.Count == 0 && notVisible.Count == 0
            ? $"Descoberta concluída: {visible.Count} assinatura(s) visível(is) e nenhuma no escopo"
              + (visible.Count == 0 ? " — a aplicação não enxerga assinatura alguma neste locatário." : " (todas desabilitadas ou fora do escopo pedido).")
              + " Isso não prova, sozinho, que falte o papel Leitor, nem que não existam recursos, nem conformidade."
            : $"Descoberta concluída: {inScope.Count} assinatura(s) consultada(s) de {visible.Count} visível(is)."
              + (notVisible.Count > 0
                  ? $" {notVisible.Count} assinatura(s) pedida(s) não está(ão) visível(is) e fica(m) no escopo como não lida(s) — a abrangência é parcial."
                  : "");
        recorder.Record(KnightCapability.AzureSubscriptions, KnightCapabilityOutcome.Collected, discoveryDetail);

        foreach (var family in Families.Where(f => f is not (KnightCapability.AzureSubscriptions or KnightCapability.AzureTenantDiagnostics)))
        {
            var perSub = outcomes.Select(kv => (Sub: kv.Key, O: kv.Value[family])).ToList();
            // [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Sem assinatura consultada, a família NÃO foi lida — antes a lista vazia dava
            // "nenhuma falha" e a família virava "coletada" sem uma única consulta de recursos.
            if (perSub.Count == 0)
            {
                recorder.Record(family, KnightCapabilityOutcome.NotAttempted,
                    notVisible.Count > 0
                        ? "Família não consultada: nenhuma das assinaturas pedidas está visível à aplicação."
                        : "Família não consultada: nenhuma assinatura no escopo desta coleta.",
                    new KnightCapabilityDiagnostic("discovery", KnightFailureKind.NoScope));
                continue;
            }
            if (family == KnightCapability.AzureAuthorization && policy is { } pf && perSub.All(p => p.O.Outcome == KnightCapabilityOutcome.Collected))
            {
                // A política de assinaturas é do locatário: a falha dela não reprova a família, mas fica dita.
                recorder.Record(family, KnightCapabilityOutcome.Collected, pf);
                continue;
            }
            var failed = perSub.Where(p => p.O.Outcome != KnightCapabilityOutcome.Collected).ToList();
            if (failed.Count == 0)
            {
                var truncated = perSub.Count(p => p.O.Truncated);
                var children = perSub.Sum(p => p.O.ChildFailures);
                var detail = string.Join(" ", new[]
                {
                    truncated > 0 ? $"A enumeração atingiu o teto em {truncated} assinatura(s)." : null,
                    children > 0 ? $"{children} leitura(s) filha(s) recusada(s) ou indisponível(is)." : null,
                    family == KnightCapability.AzureAuthorization ? users : null,
                    family == KnightCapability.AzureAppService ? stacks : null,
                }.Where(x => x is not null));
                recorder.Record(family, KnightCapabilityOutcome.Collected, detail.Length == 0 ? null : detail);
            }
            else
            {
                var first = failed[0].O;
                var read = perSub.Count - failed.Count;
                recorder.Record(family, first.Outcome,
                    KnightRuleText.Trim($"Falhou em {failed.Count} de {perSub.Count} assinatura(s)"
                        + (read > 0 ? $"; os dados das {read} assinatura(s) lidas foram preservados e a abrangência é parcial" : "")
                        + $". {first.Detail}", 480),
                    first.Diagnostic);
            }
        }
        recorder.Record(KnightCapability.AzureTenantDiagnostics, tenantDiag.Outcome, tenantDiag.Detail, tenantDiag.Diagnostic);
    }

    // ---- Leituras ------------------------------------------------------------------------------------------

    private async Task ReadFamilyAsync(string token, string sub, ArmRead read, List<Building> resources, FamilyOutcome outcome, CancellationToken ct)
    {
        var url = read.GenericType is { } gt
            ? $"{AzureArmPlan.ArmHost}/subscriptions/{sub}/resources?$filter=resourceType%20eq%20'{gt}'&api-version={read.ApiVersion}"
            : Url($"{AzureArmPlan.ArmHost}/subscriptions/{sub}/{read.ListPath}", read.ApiVersion);

        var count = 0;
        await foreach (var item in _rest.GetPagedAsync(token, url, ct))
        {
            var name = KnightJson.Str(item, "name") ?? "";
            if (read.ItemNameFilter is { } filter && !filter(name)) continue;
            if (++count > read.Max) { outcome.Truncated = true; break; }

            var source = item;
            if (read.GetByIdApiVersion is { } byIdApi && KnightJson.Str(item, "id") is { } rid)
            {
                try { source = await _rest.GetAsync(token, Url(AzureArmPlan.ArmHost + rid, byIdApi), ct); }
                catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, ct)) { outcome.ChildFailures++; }
            }

            var b = Start(source, sub, read.Family, null);
            Extract(source, read.Keep, "", b.Facts);
            foreach (var inline in read.Inline ?? Array.Empty<ArmInline>())
            {
                var list = new JsonArray();
                foreach (var e in AzureJson.Items(source, inline.ArrayPath)) list.Add(Reduce(e, inline.Keep));
                b.Facts[inline.Key + ":items"] = JsonSerializer.SerializeToElement(list);
            }
            resources.Add(b);
            await ReadChildrenAsync(token, b, read.Children, resources, outcome, ct);
        }
    }

    private async Task ReadChildrenAsync(string token, Building parent, ArmChild[] children, List<Building> resources,
        FamilyOutcome outcome, CancellationToken ct)
    {
        foreach (var child in children)
        {
            var url = Url(AzureArmPlan.ArmHost + parent.Id + child.Suffix, child.ApiVersion);
            try
            {
                switch (child.Mode)
                {
                    case ArmChildMode.Object:
                        Extract(await _rest.GetAsync(token, url, ct), child.Keep, child.Key + ":", parent.Facts);
                        break;
                    case ArmChildMode.ListToFact:
                        var list = new JsonArray();
                        var n = 0;
                        await foreach (var item in _rest.GetPagedAsync(token, url, ct))
                        {
                            if (++n > 2000) { parent.Facts[child.Key + ":truncated"] = JsonSerializer.SerializeToElement(true); break; }
                            list.Add(child.Summarize is { } summarize ? summarize(item) : Reduce(item, child.Keep));
                        }
                        parent.Facts[child.Key + ":items"] = JsonSerializer.SerializeToElement(list);
                        break;
                    case ArmChildMode.ExpandAsResources:
                        var m = 0;
                        await foreach (var item in _rest.GetPagedAsync(token, url, ct))
                        {
                            var name = KnightJson.Str(item, "name") ?? "";
                            if (child.ItemNameFilter is { } f && !f(name)) continue;
                            if (++m > 500) { outcome.Truncated = true; break; }
                            var b = Start(item, parent.SubscriptionId, parent.Family, parent.Id);
                            Extract(item, child.Keep, "", b.Facts);
                            resources.Add(b);
                            if (child.ItemChildren is { } ic) await ReadChildrenAsync(token, b, ic, resources, outcome, ct);
                        }
                        parent.Facts[child.Key + ":count"] = JsonSerializer.SerializeToElement(m);
                        break;
                }
            }
            catch (EntraGraphException ex)
            {
                // A filha que não existe (404) é um fato sobre o recurso; a recusada é uma lacuna — as duas viram status.
                parent.Facts[child.Key + ":status"] = JsonSerializer.SerializeToElement(ex.HttpStatusCode == 404 ? "NotFound" : ex.Kind.ToString());
                if (ex.HttpStatusCode != 404) outcome.ChildFailures++;
            }
            catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, ct))
            {
                // [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Tempo esgotado, circuito aberto ou transporte numa filha: o valor fica
                // desconhecido para a regra (status ≠ coletado) e o recurso pai continua valendo.
                parent.Facts[child.Key + ":status"] = JsonSerializer.SerializeToElement("Unavailable");
                outcome.ChildFailures++;
            }
        }
    }

    /// <summary>Política de certificado de cada certificado dos cofres (validade em meses). Exige papel no plano de dados.</summary>
    private async Task ReadVaultCertificatesAsync(IMicrosoftGraphCredentials cfg, List<Building> resources,
        Dictionary<string, Dictionary<KnightCapability, FamilyOutcome>> outcomes, CancellationToken ct)
    {
        var vaults = resources.Where(r => r.Type.Equals("Microsoft.KeyVault/vaults", StringComparison.OrdinalIgnoreCase)).ToList();
        if (vaults.Count == 0) return;
        string vaultToken;
        try
        {
            vaultToken = await _tokens.AcquireAsync(cfg, AzureArmPlan.VaultScope, ct);
        }
        catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, ct))
        {
            var failure = KnightFailureClassifier.Classify(ex, Label, "token", null, null);
            foreach (var f in outcomes.Values) f[KnightCapability.AzureKeyVaultCertificates].Fail(failure.Outcome, failure.Reason, failure.Diagnostic);
            return;
        }

        foreach (var v in vaults)
        {
            if (!v.Facts.TryGetValue("properties.vaultUri", out var uriEl) || uriEl.GetString() is not { Length: > 0 } uri) continue;
            var baseUri = uri.EndsWith('/') ? uri : uri + "/";
            var outcome = outcomes.TryGetValue(v.SubscriptionId, out var f) ? f[KnightCapability.AzureKeyVaultCertificates] : new FamilyOutcome();
            var items = new JsonArray();
            try
            {
                var n = 0;
                await foreach (var c in _rest.GetPagedAsync(vaultToken, $"{baseUri}certificates?api-version={AzureArmPlan.VaultDataApi}", ct))
                {
                    if (++n > MaxCertificatesPerVault) { v.Facts["certs:truncated"] = JsonSerializer.SerializeToElement(true); break; }
                    var id = KnightJson.Str(c, "id");
                    var name = id?.TrimEnd('/').Split('/').LastOrDefault();
                    if (name is null) continue;
                    var policy = await _rest.GetAsync(vaultToken, $"{baseUri}certificates/{Uri.EscapeDataString(name)}/policy?api-version={AzureArmPlan.VaultDataApi}", ct);
                    var months = KnightJson.Int(KnightJson.Prop(policy, "x509_props"), "validity_months");
                    items.Add(new JsonObject { ["name"] = name, ["validityMonths"] = months });
                }
                v.Facts["certs:items"] = JsonSerializer.SerializeToElement(items);
            }
            catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, ct))
            {
                v.Facts["certs:status"] = JsonSerializer.SerializeToElement(ex is EntraGraphException g ? g.Kind.ToString() : "Unavailable");
                var failure = KnightFailureClassifier.Classify(ex, Label, "read", null, null);
                outcome.Fail(failure.Outcome, $"certificados do cofre {v.Name}: {failure.Reason}", failure.Diagnostic);
            }
        }
    }

    private async Task<(KnightCapabilityOutcome Outcome, string? Detail, KnightCapabilityDiagnostic? Diagnostic)> ReadTenantListAsync(
        string token, ArmRead read, List<Building> resources, CancellationToken ct)
    {
        var url = Url($"{AzureArmPlan.ArmHost}/{read.ListPath}", read.ApiVersion);
        try
        {
            var n = 0;
            await foreach (var item in _rest.GetPagedAsync(token, url, ct))
            {
                var b = Start(item, "", read.Family, null);
                Extract(item, read.Keep, "", b.Facts);
                resources.Add(b);
                n++;
            }
            resources.Add(Marker("aegis:tenant/" + read.Family, "AEGIS/TenantRead", read.Family, "Collected", n));
            return (KnightCapabilityOutcome.Collected, null, null);
        }
        catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, ct))
        {
            var failure = KnightFailureClassifier.Classify(ex, Label, "read", null, null);
            return (failure.Outcome, $"{read.Label}: {failure.Reason}", failure.Diagnostic);
        }
    }

    /// <summary>Pilhas de runtime publicadas pela Microsoft (escopo do provedor), achatadas em versões.</summary>
    private async Task<string?> ReadRuntimeStacksAsync(string token, List<Building> resources, CancellationToken ct)
    {
        var failed = new List<string>();
        foreach (var (type, path) in AzureArmPlan.RuntimeStacks)
        {
            try
            {
                await foreach (var stack in _rest.GetPagedAsync(token, Url($"{AzureArmPlan.ArmHost}/{path}", "2023-12-01"), ct))
                {
                    var name = KnightJson.Str(stack, "name") ?? "";
                    var b = new Building
                    {
                        Id = $"/{path}/{name}", Type = type, Name = name, SubscriptionId = "", Family = KnightCapability.AzureAppService,
                    };
                    b.Facts["versions:items"] = JsonSerializer.SerializeToElement(FlattenStack(stack));
                    resources.Add(b);
                }
                resources.Add(Marker("aegis:tenant/" + type, "AEGIS/TenantRead", KnightCapability.AzureAppService, "Collected", 0));
            }
            catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, ct))
            {
                var reason = KnightFailureClassifier.Classify(ex, Label, "read").Reason;
                resources.Add(Marker("aegis:tenant/" + type, "AEGIS/TenantRead", KnightCapability.AzureAppService, "Failed", 0));
                failed.Add($"{type}: {reason}");
            }
        }
        return failed.Count == 0 ? null : "Pilhas de runtime do App Service não lidas — " + string.Join(" ", failed);
    }

    /// <summary>
    /// Uma linha por versão menor e sistema: <c>os</c>, <c>runtimeVersion</c>, <c>linuxFxVersion</c> (quando a pilha o
    /// declara), <c>isDeprecated</c>, <c>endOfLifeDate</c>.
    /// </summary>
    internal static JsonArray FlattenStack(JsonElement stack)
    {
        var rows = new JsonArray();
        foreach (var major in AzureJson.Items(stack, "properties.majorVersions"))
        foreach (var minor in AzureJson.Items(major, "minorVersions"))
        foreach (var (os, settingsPath) in new[] { ("linux", "stackSettings.linuxRuntimeSettings"), ("windows", "stackSettings.windowsRuntimeSettings"),
                     ("linux", "stackSettings.linuxContainerSettings"), ("windows", "stackSettings.windowsContainerSettings") })
        {
            if (AzureJson.Prop(minor, settingsPath) is not { ValueKind: JsonValueKind.Object } st) continue;
            var row = new JsonObject
            {
                ["os"] = os,
                ["runtimeVersion"] = AzureJson.Str(st, "runtimeVersion"),
                ["linuxFxVersion"] = AzureJson.Str(st, "siteConfigPropertiesDictionary.linuxFxVersion")
                    ?? AzureJson.Str(st, "java11Runtime") ?? AzureJson.Str(st, "java8Runtime"),
                ["javaVersion"] = AzureJson.Str(st, "siteConfigPropertiesDictionary.javaVersion"),
                ["isDeprecated"] = AzureJson.Bool(st, "isDeprecated"),
                ["endOfLifeDate"] = AzureJson.Str(st, "endOfLifeDate"),
            };
            rows.Add(row);
        }
        return rows;
    }

    private async Task<string?> ReadSubscriptionPolicyAsync(string token, List<Building> resources, CancellationToken ct)
    {
        try
        {
            var p = await _rest.GetAsync(token, Url($"{AzureArmPlan.ArmHost}/{AzureArmPlan.SubscriptionPolicyPath}", AzureArmPlan.SubscriptionPolicyApi), ct);
            var b = new Building
            {
                Id = "/providers/Microsoft.Subscription/policies/default", Type = "Microsoft.Subscription/policies", Name = "default",
                SubscriptionId = "", Family = KnightCapability.AzureAuthorization,
            };
            Extract(p, new[] { "properties.blockSubscriptionsLeavingTenant", "properties.blockSubscriptionsIntoTenant",
                "properties.exemptedPrincipals" }, "", b.Facts);
            resources.Add(b);
            return null;
        }
        catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, ct))
        {
            return $"Política de assinaturas do locatário não lida: {KnightFailureClassifier.Classify(ex, Label, "read").Reason}";
        }
    }

    /// <summary>Estado (habilitada?) das contas de USUÁRIO com papel no Azure, pelo Microsoft Graph (User.Read.All).</summary>
    private async Task<string?> ReadRoleAssignmentUsersAsync(IMicrosoftGraphCredentials cfg, List<Building> resources, CancellationToken ct)
    {
        var ids = resources
            .Where(r => r.Type.Equals("Microsoft.Authorization/roleAssignments", StringComparison.OrdinalIgnoreCase)
                        && r.Facts.TryGetValue("properties.principalType", out var t) && t.GetString() == "User"
                        && r.Facts.ContainsKey("properties.principalId"))
            .Select(r => r.Facts["properties.principalId"].GetString()!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0) return null;
        var truncated = ids.Count > MaxGraphUsers;
        string graphToken;
        try
        {
            graphToken = await _graph.AcquireTokenAsync(cfg, ct);
        }
        catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, ct))
        {
            var reason = KnightFailureClassifier.Classify(ex, Label, "token").Reason;
            resources.Add(Marker("aegis:tenant/graphUsers", "AEGIS/TenantRead", KnightCapability.AzureAuthorization, "Failed", 0));
            return $"Estado das contas com papel no Azure não lido: {reason}";
        }

        var read = 0;
        foreach (var id in ids.Take(MaxGraphUsers))
        {
            try
            {
                var u = await _graph.GetJsonAsync(graphToken, cfg, $"users/{Uri.EscapeDataString(id)}?$select=id,accountEnabled,userPrincipalName,displayName", ct);
                var b = new Building
                {
                    Id = "graph:users/" + id, Type = "Microsoft.Graph/users", Name = KnightJson.Str(u, "userPrincipalName") ?? id,
                    SubscriptionId = "", Family = KnightCapability.AzureAuthorization,
                };
                Extract(u, new[] { "accountEnabled", "userPrincipalName", "displayName" }, "", b.Facts);
                resources.Add(b);
                read++;
            }
            catch (EntraGraphException ex) when (ex.HttpStatusCode == 404)
            {
                var b = new Building { Id = "graph:users/" + id, Type = "Microsoft.Graph/users", Name = id, SubscriptionId = "", Family = KnightCapability.AzureAuthorization };
                b.Facts["status"] = JsonSerializer.SerializeToElement("NotFound");
                resources.Add(b);
            }
            catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, ct))
            {
                var reason = KnightFailureClassifier.Classify(ex, Label, "read").Reason;
                resources.Add(Marker("aegis:tenant/graphUsers", "AEGIS/TenantRead", KnightCapability.AzureAuthorization, "Failed", read));
                return $"Estado das contas com papel no Azure lido em parte ({read} de {ids.Count}): {reason}";
            }
        }
        resources.Add(Marker("aegis:tenant/graphUsers", "AEGIS/TenantRead", KnightCapability.AzureAuthorization,
            truncated ? "Truncated" : "Collected", read));
        return truncated ? $"Estado lido para {MaxGraphUsers} de {ids.Count} conta(s) com papel no Azure (teto da coleta)." : null;
    }

    // ---- Utilitários ---------------------------------------------------------------------------------------

    private static Building Marker(string id, string type, KnightCapability family, string status, int count)
    {
        var b = new Building { Id = id, Type = type, Name = id, SubscriptionId = "", Family = family };
        b.Facts["status"] = JsonSerializer.SerializeToElement(status);
        b.Facts["count"] = JsonSerializer.SerializeToElement(count);
        return b;
    }

    internal static string Url(string baseUrl, string apiVersion) =>
        baseUrl + (baseUrl.Contains('?') ? "&" : "?") + "api-version=" + apiVersion;

    private static Building Start(JsonElement item, string sub, KnightCapability family, string? parentId)
    {
        var id = KnightJson.Str(item, "id") ?? "";
        return new Building
        {
            Id = id,
            Type = KnightJson.Str(item, "type") ?? "",
            Name = KnightJson.Str(item, "name") ?? id.Split('/').LastOrDefault() ?? "",
            SubscriptionId = sub,
            ResourceGroup = ResourceGroupOf(id),
            Location = KnightJson.Str(item, "location"),
            Kind = KnightJson.Str(item, "kind"),
            Family = family,
            ParentId = parentId,
        };
    }

    internal static string? ResourceGroupOf(string id)
    {
        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.FindIndex(parts, p => p.Equals("resourceGroups", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < parts.Length ? parts[i + 1] : null;
    }

    /// <summary>Copia só os caminhos pedidos (sem distinção de caixa) para os fatos, com o prefixo dado.</summary>
    internal static void Extract(JsonElement item, IEnumerable<string> paths, string prefix, Dictionary<string, JsonElement> facts)
    {
        foreach (var path in paths)
            if (AzureJson.Prop(item, path) is { } v)
                facts[prefix + path] = v.Clone();
    }

    /// <summary>Reduz um item de lista aos caminhos pedidos, preservando a estrutura aninhada.</summary>
    internal static JsonObject Reduce(JsonElement item, IEnumerable<string> paths)
    {
        var root = new JsonObject();
        foreach (var path in paths)
        {
            if (AzureJson.Prop(item, path) is not { } v) continue;
            var parts = path.Split('.');
            var cur = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (cur[parts[i]] is not JsonObject next) { next = new JsonObject(); cur[parts[i]] = next; }
                cur = next;
            }
            cur[parts[^1]] = JsonNode.Parse(v.GetRawText());
        }
        return root;
    }
}

/// <summary>Corte de texto para os motivos gravados.</summary>
internal static class KnightRuleText
{
    public static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
