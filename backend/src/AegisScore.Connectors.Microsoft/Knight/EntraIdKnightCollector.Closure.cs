using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Connectors.Microsoft.Knight.Services;
using AegisScore.Application.Knight.Configuration;

namespace AegisScore.Connectors.Microsoft.Knight;

// ============================================================================
//  [AEGIS-KNIGHT-CLOSURE-01] Leituras em versão BETA do Microsoft Graph e sessão ociosa do Microsoft 365
// ============================================================================
// A Microsoft não suporta a versão beta em produção: cada leitura beta é uma capacidade PRÓPRIA, com o contrato documentado
// conferido aqui. Recusa, indisponibilidade ou resposta fora do contrato viram limitação da capacidade, nomeando a versão —
// os controles que dependem dela ficam não avaliados, nunca aprovados.
//
// Permissões de aplicativo, todas de LEITURA:
//   • Policy.Read.All                       → estado da MFA por usuário (beta), campos beta da política de métodos de
//                                             autenticação e políticas de tempo limite por inatividade (v1.0);
//   • User.Read.All                         → enumeração das contas habilitadas (já exigida pela coleta de identidade);
//   • OrgSettings-AppsAndServices.Read.All  → aplicativos e serviços próprios dos usuários (beta);
//   • OrgSettings-Forms.Read.All            → configurações do Microsoft Forms (beta).

public sealed partial class EntraIdKnightCollector
{
    internal const string GraphBeta = "https://graph.microsoft.com/beta/";
    internal const string EnabledUsersUrl = "users?$filter=accountEnabled eq true&$select=id,userPrincipalName,displayName&$top=999";
    internal const string AuthenticationMethodsPolicyBetaUrl = GraphBeta + "policies/authenticationMethodsPolicy";
    internal const string AppsAndServicesBetaUrl = GraphBeta + "admin/appsAndServices";
    internal const string FormsBetaUrl = GraphBeta + "admin/forms";
    internal const string ActivityTimeoutPoliciesUrl = "policies/activityBasedTimeoutPolicies";

    internal static string PerUserMfaUrl(string userId) => GraphBeta + $"users/{Uri.EscapeDataString(userId)}/authentication/requirements";

    /// <summary>Leituras usuário a usuário em paralelo (limite baixo para não provocar limitação de taxa).</summary>
    internal const int PerUserParallelism = 6;

    /// <summary>
    /// [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Orçamento de tempo da leitura usuário a usuário. É a única leitura N+1 da coleta:
    /// sem teto de tempo, um locatário grande ou limitado pelo serviço prendia a sincronização inteira. Estourado, a leitura
    /// para, o que foi lido é preservado e as contas restantes ficam declaradas como não lidas (a regra só aprova com tudo lido).
    /// </summary>
    internal TimeSpan PerUserMfaBudget { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>Observação de leitura parcial da MFA por usuário, aplicada à capacidade depois que ela conclui.</summary>
    private sealed class PartialRead
    {
        public string? Detail;
        public KnightCapabilityDiagnostic? Diagnostic;
    }

    /// <summary>Resposta que não segue o contrato documentado da versão lida.</summary>
    private sealed class GraphContractException : Exception
    {
        public GraphContractException(string message) : base(message) { }
    }

    private async Task CollectClosureAsync(string token, KnightEntraIdConfiguration cfg, List<KnightObservation> obs,
        List<KnightCapabilityStatus> caps, List<KnightConfigurationDocument> docs, EntraRun run)
    {
        var ct = run.Ct;
        var partial = new PartialRead();
        await RunContractAsync(KnightCapability.PerUserMfaStates, "versão beta do Microsoft Graph, /users/{id}/authentication/requirements",
            docs, add => CollectPerUserMfaAsync(token, cfg, add, run, partial), obs, caps, run);
        // [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Leitura concluída, mas parcial: a capacidade diz isso, com o diagnóstico.
        var mfa = caps.FindLastIndex(c => c.Capability == KnightCapability.PerUserMfaStates);
        if (mfa >= 0 && caps[mfa].Outcome == KnightCapabilityOutcome.Collected && partial.Detail is not null)
            caps[mfa] = caps[mfa] with { Detail = partial.Detail, Diagnostic = partial.Diagnostic };

        await RunContractAsync(KnightCapability.AuthenticationMethodsPolicyPreview, "versão beta do Microsoft Graph, /policies/authenticationMethodsPolicy",
            docs, async add =>
            {
                var root = await _graph.GetJsonAsync(token, cfg, AuthenticationMethodsPolicyBetaUrl, ct);
                if (root.ValueKind != JsonValueKind.Object) throw new GraphContractException("a resposta não é um objeto");
                var prefs = Obj(root, "systemCredentialPreferences");
                if (prefs.ValueKind != JsonValueKind.Object) throw new GraphContractException("sem o objeto systemCredentialPreferences");
                var configs = Obj(root, "authenticationMethodConfigurations");
                if (configs.ValueKind != JsonValueKind.Array) throw new GraphContractException("sem a lista authenticationMethodConfigurations");
                var authenticator = configs.EnumerateArray()
                    .FirstOrDefault(m => string.Equals(Str(m, "id"), "MicrosoftAuthenticator", StringComparison.OrdinalIgnoreCase));
                EntraPreviewFeature? companion = null;
                if (authenticator.ValueKind == JsonValueKind.Object
                    && Obj(Obj(authenticator, "featureSettings"), "companionAppAllowedState") is { ValueKind: JsonValueKind.Object } c)
                    companion = new EntraPreviewFeature(Str(c, "state"), TargetId(c, "includeTarget"), TargetId(c, "excludeTarget"));
                add(KnightTenantConfiguration.Document(EntraAuthenticationMethodsPreview.ExternalId, "Política de métodos de autenticação (versão beta)",
                    new EntraAuthenticationMethodsPreview(
                        new EntraPreviewFeature(Str(prefs, "state"), TargetIdsOf(prefs, "includeTargets"), TargetIdsOf(prefs, "excludeTargets")),
                        authenticator.ValueKind == JsonValueKind.Object, authenticator.ValueKind == JsonValueKind.Object ? Str(authenticator, "state") : null,
                        companion)));
            }, obs, caps, run);

        await RunContractAsync(KnightCapability.M365AppsAndServicesSettings, "versão beta do Microsoft Graph, /admin/appsAndServices",
            docs, async add =>
            {
                var settings = AdminSettings(await _graph.GetJsonAsync(token, cfg, AppsAndServicesBetaUrl, ct));
                add(KnightTenantConfiguration.Document(M365AppsAndServicesSettings.ExternalId, "Aplicativos e serviços próprios dos usuários",
                    new M365AppsAndServicesSettings(Bool(settings, "isOfficeStoreEnabled"), Bool(settings, "isAppAndServicesTrialEnabled"))));
            }, obs, caps, run);

        await RunContractAsync(KnightCapability.M365FormsSettings, "versão beta do Microsoft Graph, /admin/forms",
            docs, async add =>
            {
                var settings = AdminSettings(await _graph.GetJsonAsync(token, cfg, FormsBetaUrl, ct));
                add(KnightTenantConfiguration.Document(M365FormsSettings.ExternalId, "Configurações do Microsoft Forms",
                    new M365FormsSettings(Bool(settings, "isInOrgFormsPhishingScanEnabled"), Bool(settings, "isExternalSendFormEnabled"),
                        Bool(settings, "isExternalShareCollaborationEnabled"))));
            }, obs, caps, run);

        await RunContractAsync(KnightCapability.ActivityBasedTimeoutPolicy, "Microsoft Graph v1.0, /policies/activityBasedTimeoutPolicies",
            docs, async add =>
            {
                await foreach (var p in _graph.GetPagedAsync(token, cfg, ActivityTimeoutPoliciesUrl, ct))
                {
                    if (Str(p, "id") is not { } id) continue;
                    var (apps, limitation) = TimeoutApplications(p);
                    add(KnightTenantConfiguration.Document(id, Str(p, "displayName"),
                        new EntraActivityTimeoutPolicy(id, Str(p, "displayName"), Bool(p, "isOrganizationDefault"), apps, limitation)));
                }
            }, obs, caps, run);
    }

    /// <summary>
    /// Executa uma capacidade de configuração e, se a resposta sair do contrato documentado, substitui o motivo genérico de
    /// erro pelo nome da versão e do desvio.
    /// </summary>
    private async Task RunContractAsync(KnightCapability capability, string api, List<KnightConfigurationDocument> docs,
        Func<Action<KnightConfigurationDocument>, Task> collect, List<KnightObservation> obs, List<KnightCapabilityStatus> caps, EntraRun run)
    {
        string? contract = null;
        await RunConfigAsync(capability, docs, async add =>
        {
            try { await collect(add); }
            catch (GraphContractException ex) { contract = ex.Message; throw; }
        }, obs, caps, Array.Empty<KnightSignalKey>(), run);
        if (contract is null) return;
        var i = caps.FindLastIndex(c => c.Capability == capability);
        if (i >= 0)
            caps[i] = new KnightCapabilityStatus(capability, KnightCapabilityOutcome.Error,
                $"Resposta fora do contrato documentado ({api}): {contract}. Os controles que dependem desta leitura ficam não avaliados.",
                new KnightCapabilityDiagnostic("read", KnightFailureKind.Contract, caps[i].Diagnostic?.DurationMs, 200,
                    "GraphContract", null, run.CorrelationId));
    }

    /// <summary>
    /// <c>/admin/appsAndServices</c> e <c>/admin/forms</c> devolvem o objeto ora na raiz, ora dentro de <c>value</c> (como no
    /// exemplo da própria documentação). O que importa é o objeto <c>settings</c>; sem ele, a resposta saiu do contrato.
    /// </summary>
    private static JsonElement AdminSettings(JsonElement root)
    {
        var holder = root.ValueKind == JsonValueKind.Object && Obj(root, "value") is { ValueKind: JsonValueKind.Object } v ? v : root;
        var settings = Obj(holder, "settings");
        if (settings.ValueKind != JsonValueKind.Object) throw new GraphContractException("sem o objeto settings");
        return settings;
    }

    private static IReadOnlyList<string> TargetIdsOf(JsonElement parent, string prop) =>
        Obj(parent, prop) is { ValueKind: JsonValueKind.Array } a
            ? a.EnumerateArray().Select(t => Str(t, "id")).OfType<string>().ToList()
            : Array.Empty<string>();

    private static IReadOnlyList<string> TargetId(JsonElement parent, string prop) =>
        Str(Obj(parent, prop), "id") is { } id ? new[] { id } : Array.Empty<string>();

    /// <summary>
    /// A definição de uma política de inatividade é uma lista de TEXTOS JSON (<c>ActivityBasedTimeoutPolicy.ApplicationPolicies</c>).
    /// Texto ilegível vira limitação do documento, sem tempo limite inventado.
    /// </summary>
    internal static (IReadOnlyList<EntraActivityTimeoutApplication> Apps, string? Limitation) TimeoutApplications(JsonElement policy)
    {
        var apps = new List<EntraActivityTimeoutApplication>();
        var definitions = Obj(policy, "definition");
        if (definitions.ValueKind != JsonValueKind.Array) return (apps, "a política não trouxe a definição.");
        try
        {
            foreach (var d in definitions.EnumerateArray())
            {
                if (d.ValueKind != JsonValueKind.String) continue;
                using var doc = JsonDocument.Parse(d.GetString() ?? "{}");
                var abt = Obj(doc.RootElement, "ActivityBasedTimeoutPolicy");
                foreach (var a in Obj(abt, "ApplicationPolicies") is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray() : Enumerable.Empty<JsonElement>())
                    apps.Add(new EntraActivityTimeoutApplication(Str(a, "ApplicationId"), Str(a, "WebSessionIdleTimeout")));
            }
        }
        catch (JsonException)
        {
            return (apps, "a definição da política não é um JSON legível.");
        }
        return (apps, apps.Count == 0 ? "a definição não tem tempo limite por aplicação." : null);
    }

    private async Task CollectPerUserMfaAsync(string token, KnightEntraIdConfiguration cfg, Action<KnightConfigurationDocument> add,
        EntraRun run, PartialRead partial)
    {
        var ct = run.Ct;
        var started = _time.GetTimestamp();
        var users = new List<(string Id, string? Upn, string? Name)>();
        var truncated = false;
        await foreach (var u in _graph.GetPagedAsync(token, cfg, EnabledUsersUrl, ct))
        {
            if (Str(u, "id") is not { } id) continue;
            if (users.Count >= EntraPerUserMfaInventory.MaxAccounts) { truncated = true; break; }
            users.Add((id, Str(u, "userPrincipalName"), Str(u, "displayName")));
        }

        var states = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        var removedIds = new ConcurrentBag<string>();
        var unread = 0;
        EntraGraphException? fatal = null;
        string? contract = null;
        // [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Falha SISTÊMICA (tempo esgotado do pipeline, circuito aberto, transporte): antes
        // escapava do laço como exceção não tratada e a capacidade inteira virava "erro inesperado", jogando fora as contas já
        // lidas. Agora interrompe o laço (sem continuar martelando o serviço), e o que foi lido é preservado.
        Exception? systemic = null;
        using var budget = new CancellationTokenSource(PerUserMfaBudget, _time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        try
        {
            await Parallel.ForEachAsync(users, new ParallelOptions { MaxDegreeOfParallelism = PerUserParallelism, CancellationToken = stop.Token },
                async (u, token2) =>
                {
                    try
                    {
                        var r = await _graph.GetJsonAsync(token, cfg, PerUserMfaUrl(u.Id), token2);
                        var state = r.ValueKind == JsonValueKind.Object ? Str(r, "perUserMfaState") : null;
                        if (state is null) { contract ??= "sem a propriedade perUserMfaState"; stop.Cancel(); return; }
                        states[u.Id] = state;
                    }
                    catch (EntraGraphException ex) when (ex.HttpStatusCode == 404)
                    {
                        Interlocked.Increment(ref removed); // conta excluída entre a listagem e a leitura
                        removedIds.Add(u.Id);
                    }
                    catch (EntraGraphException ex) when (ex.Kind is EntraGraphErrorKind.InsufficientPermission or EntraGraphErrorKind.AuthFailure)
                    {
                        fatal ??= ex;
                        stop.Cancel();
                    }
                    catch (EntraGraphException)
                    {
                        Interlocked.Increment(ref unread); // limitação de taxa ou indisponibilidade pontual: a conta fica não lida
                    }
                    catch (Exception ex) when (!token2.IsCancellationRequested)
                    {
                        Interlocked.CompareExchange(ref systemic, ex, null);
                        stop.Cancel();
                    }
                    catch (Exception) when (token2.IsCancellationRequested)
                    {
                        // Leitura em curso quando o laço foi interrompido: a conta fica não lida (o motivo é o da interrupção).
                    }
                });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Interrompido por recusa de autorização, resposta fora do contrato, falha sistêmica ou orçamento (tratados abaixo).
        }

        ct.ThrowIfCancellationRequested();
        if (fatal is not null) throw fatal;
        if (contract is not null) throw new GraphContractException(contract);

        var accounts = users.Count - removed;
        // Nada lido e a leitura interrompida por falha sistêmica: a falha é o desfecho (classificado), não um inventário vazio.
        if (systemic is not null && states.IsEmpty && accounts > 0) throw systemic;

        unread = Math.Max(0, accounts - states.Count);
        string? interruption = null;
        if (systemic is not null)
        {
            var f = KnightFailureClassifier.Classify(systemic, "Microsoft Graph", "read", Elapsed(started), run.CorrelationId);
            interruption = $"Leitura interrompida depois de {states.Count} de {accounts} conta(s): {f.Reason}";
            partial.Diagnostic = f.Diagnostic with { Kind = KnightFailureKind.Incomplete, Code = f.Diagnostic.Code ?? f.Diagnostic.Kind.ToString() };
            LogFailure(KnightCapability.PerUserMfaStates.ToString(), f, run);
        }
        else if (budget.IsCancellationRequested && unread > 0)
        {
            interruption = $"Leitura interrompida pelo orçamento de tempo de {PerUserMfaBudget.TotalMinutes:0.#} minuto(s) depois de "
                + $"{states.Count} de {accounts} conta(s).";
            partial.Diagnostic = KnightFailureClassifier.Incomplete("read", Elapsed(started), run.CorrelationId, "TimeBudget");
        }
        if (interruption is not null)
            partial.Detail = interruption + " As contas lidas foram preservadas; as demais ficam não lidas e a regra só aprova com todas lidas.";
        else if (unread > 0)
        {
            // Contas não lidas por limitação de taxa ou indisponibilidade pontual: a leitura terminou, mas é PARCIAL — e diz isso.
            partial.Detail = $"Leitura parcial: {states.Count} de {accounts} conta(s) lida(s); {unread} não lida(s) por limitação de taxa ou "
                + "indisponibilidade pontual do Microsoft Graph. A regra só aprova com todas lidas.";
            partial.Diagnostic = KnightFailureClassifier.Incomplete("read", Elapsed(started), run.CorrelationId, "Unread");
        }
        var notDisabled = users
            .Where(u => states.TryGetValue(u.Id, out var s) && !string.Equals(s, "disabled", StringComparison.OrdinalIgnoreCase))
            .Select(u => new EntraPerUserMfaUser(u.Id, u.Upn, u.Name, states[u.Id]))
            .OrderBy(u => u.UserPrincipalName ?? u.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        int Count(string s) => states.Values.Count(v => string.Equals(v, s, StringComparison.OrdinalIgnoreCase));
        add(KnightTenantConfiguration.Document(EntraPerUserMfaInventory.ExternalId, "MFA por usuário (legado)",
            new EntraPerUserMfaInventory(accounts, states.Count, Count("disabled"), Count("enabled"), Count("enforced"), unread, truncated,
                notDisabled.Take(EntraPerUserMfaInventory.MaxListed).ToList(),
                truncated ? $"A enumeração parou no teto de {EntraPerUserMfaInventory.MaxAccounts} contas habilitadas."
                    : interruption is not null ? $"{unread} conta(s) não lida(s). {interruption}"
                    : unread > 0 ? $"{unread} conta(s) não lida(s) (limitação de taxa ou indisponibilidade do Microsoft Graph)." : null,
                // [AEGIS-KNIGHT-COLLECTION-GAPS-01] A população enumerada (sem consulta por usuário a mais), para a base mínima.
                users.Select(u => u.Id).Except(removedIds, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList())));
    }
}
