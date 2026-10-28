using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Identity.Adm;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Connectors.Microsoft.Knight.Services;
using Microsoft.Extensions.Logging;

namespace AegisScore.Connectors.Microsoft.Knight;

// ============================================================================
//  [AEGIS-KNIGHT-COLLECTION-GAPS-01] Pertencimento dos grupos referenciados (Entra ID)
// ============================================================================
// O relatório de 27/10 deixou AK-ENTRA-008/014 sem avaliação porque o alcance das políticas dependia de grupos cujo
// pertencimento não era lido, e AK-ENTRA-001/011 porque grupos atribuídos a papéis viravam "membro não identificável".
// Aqui o pertencimento TRANSITIVO (GET /groups/{id}/transitiveMembers, Directory.Read.All — já consentida) é lido para os
// grupos efetivamente referenciados: inclusões e exclusões das políticas HABILITADAS e grupos atribuídos a papéis.
//   • cada grupo é lido UMA vez por coleta (cache que vive só nesta execução, de um locatário) e reaproveitado;
//   • a paginação segue o @odata.nextLink validado do cliente; o teto vira leitura INCOMPLETA, nunca lista completa;
//   • recusa (grupo de pertencimento oculto sem Member.Read.Hidden, que o AEGIS não solicita), 404 e falha ficam no
//     documento do grupo, com o motivo: o grupo é DESCONHECIDO para quem não foi listado — nunca vazio;
//   • nome e UPN só são guardados para contas privilegiadas; os demais membros ficam só pelo id e tipo.

public sealed partial class EntraIdKnightCollector
{
    /// <summary>Teto de grupos lidos numa coleta (os excedentes ficam registrados como não lidos).</summary>
    internal const int MaxGroupsPerCollection = 200;

    /// <summary>Máximo de identificadores num filtro <c>in</c> do Graph (limite documentado de 15 valores).</summary>
    private const int MaxIdsPerUserFilter = 15;

    internal static string TransitiveMembersUrl(string groupId) => $"groups/{Uri.EscapeDataString(groupId)}/transitiveMembers";

    internal static string PrivilegedUsersUrl(IEnumerable<string> ids, bool withActivity) =>
        "users?$filter=id in (" + string.Join(",", ids.Select(i => $"'{i}'")) + ")"
        + "&$select=id,userType,displayName,userPrincipalName" + (withActivity ? ",signInActivity" : "");

    /// <summary>Leitor de pertencimento de UMA coleta: cada grupo lido uma vez, com a procedência acumulada.</summary>
    private sealed class GroupReader
    {
        private readonly EntraIdKnightCollector _collector;
        private readonly string _token;
        private readonly KnightEntraIdConfiguration _cfg;
        private readonly EntraRun _run;
        private readonly DateTimeOffset _now;
        private readonly Dictionary<string, GroupRead> _cache = new(StringComparer.OrdinalIgnoreCase);

        public GroupReader(EntraIdKnightCollector collector, string token, KnightEntraIdConfiguration cfg, EntraRun run, DateTimeOffset now)
        {
            _collector = collector;
            _token = token;
            _cfg = cfg;
            _run = run;
            _now = now;
        }

        /// <summary>Contas privilegiadas desta coleta: as únicas cujo nome/UPN vai ao documento do grupo.</summary>
        public HashSet<string> Privileged { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, (string? Name, string? Upn)> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<GroupRead> All => _cache.Values;

        public async Task<GroupRead> ReadAsync(string groupId, string? displayName, IEnumerable<string> referencedBy, CancellationToken ct)
        {
            if (_cache.TryGetValue(groupId, out var cached))
            {
                cached.ReferencedBy.UnionWith(referencedBy);
                cached.DisplayName ??= displayName;
                return cached;
            }

            var read = new GroupRead(groupId, displayName, _now);
            read.ReferencedBy.UnionWith(referencedBy);
            _cache[groupId] = read;
            if (_cache.Count > MaxGroupsPerCollection)
            {
                read.State = EntraGroupMembershipState.Failed;
                read.Detail = $"Não lido: teto de {MaxGroupsPerCollection} grupos por coleta.";
                return read;
            }

            try
            {
                await foreach (var m in _collector._graph.GetPagedAsync(_token, _cfg, TransitiveMembersUrl(groupId), ct))
                {
                    var id = Str(m, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    read.Members.Add(new EntraGroupMember(id, EntraMemberKind.FromODataType(Str(m, "@odata.type")),
                        Str(m, "displayName"), Str(m, "userPrincipalName")));
                }
                read.State = EntraGroupMembershipState.Complete;
            }
            catch (Exception ex) when (KnightFailureClassifier.IsCallerCancellation(ex, ct))
            {
                throw;
            }
            catch (EntraGraphException ex)
            {
                // Os membros lidos antes da falha CONTINUAM membros (prova positiva); só os demais ficam desconhecidos.
                read.State = ex.Kind switch
                {
                    EntraGraphErrorKind.Incomplete => EntraGroupMembershipState.Incomplete,
                    EntraGraphErrorKind.InsufficientPermission => EntraGroupMembershipState.Denied,
                    _ when ex.HttpStatusCode == 404 => EntraGroupMembershipState.NotFound,
                    _ => EntraGroupMembershipState.Failed,
                };
                read.HttpStatus = ex.HttpStatusCode;
                read.Code = ex.GraphErrorCode;
                var f = KnightFailureClassifier.FromHttp(ex, "Microsoft Graph", "read", null, _run.CorrelationId);
                read.Detail = read.State == EntraGroupMembershipState.Denied
                    ? f.Reason + " Grupo de pertencimento oculto exige Member.Read.Hidden, que o AEGIS não solicita."
                    : f.Reason;
                _collector.LogFailure(KnightCapability.EntraGroupMembership.ToString(), f, _run);
            }
            catch (Exception ex)
            {
                read.State = EntraGroupMembershipState.Failed;
                var f = KnightFailureClassifier.Classify(ex, "Microsoft Graph", "read", null, _run.CorrelationId);
                read.Detail = f.Reason;
                _collector.LogFailure(KnightCapability.EntraGroupMembership.ToString(), f, _run);
            }

            // Nome do grupo referenciado só por id numa política: leitura auxiliar, que nunca decide o pertencimento.
            if (read.DisplayName is null && read.State != EntraGroupMembershipState.NotFound)
            {
                try
                {
                    var g = await _collector._graph.GetJsonAsync(_token, _cfg, $"groups/{Uri.EscapeDataString(groupId)}?$select=id,displayName", ct);
                    read.DisplayName = Str(g, "displayName");
                }
                catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, ct))
                {
                    _collector._log?.LogDebug("Nome do grupo {Group} não lido (correlação {CorrelationId}).", groupId, _run.CorrelationId);
                }
            }
            return read;
        }

        public string Describe(string groupId) => _cache.TryGetValue(groupId, out var r)
            ? $"“{r.DisplayName ?? groupId}” ({EntraGroupMembership.Label(r.State)})"
            : $"“{groupId}”";

        public IReadOnlyList<EntraGroupMembership> ToContracts() => _cache.Values
            .OrderBy(r => r.GroupId, StringComparer.Ordinal)
            .Select(r => new EntraGroupMembership(
                r.GroupId, r.DisplayName, r.State,
                r.Members
                    .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                    .Select(m => m.Kind == EntraMemberKind.User && Privileged.Contains(m.Id)
                        ? new EntraGroupMember(m.Id, m.Kind,
                            (Names.TryGetValue(m.Id, out var n) ? n.Name : null) ?? m.DisplayName,
                            (Names.TryGetValue(m.Id, out var n2) ? n2.Upn : null) ?? m.UserPrincipalName)
                        : new EntraGroupMember(m.Id, m.Kind))
                    .ToList(),
                r.ReferencedBy.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                r.ReadAt, "/v1.0/groups/" + r.GroupId + "/transitiveMembers", r.Detail, r.HttpStatus, r.Code))
            .ToList();
    }

    private sealed class GroupRead
    {
        public GroupRead(string groupId, string? displayName, DateTimeOffset readAt)
        {
            GroupId = groupId;
            DisplayName = displayName;
            ReadAt = readAt;
        }

        public string GroupId { get; }
        public string? DisplayName { get; set; }
        public DateTimeOffset ReadAt { get; }
        public EntraGroupMembershipState State { get; set; } = EntraGroupMembershipState.Failed;
        public List<EntraGroupMember> Members { get; } = new();
        public HashSet<string> ReferencedBy { get; } = new(StringComparer.Ordinal);
        public string? Detail { get; set; }
        public int? HttpStatus { get; set; }
        public string? Code { get; set; }
    }

    /// <summary>
    /// Completa as contas privilegiadas que vieram sem atividade (ou só via grupo, sem tipo nem nome) pelo filtro
    /// <c>id in (...)</c> de /users, até 15 por chamada. Sem <c>signInActivity</c> (licença ou permissão), tenta de novo sem
    /// ele: o nome e o tipo continuam úteis, e a atividade fica ausente — o controle diz isso, nunca "inativa".
    /// </summary>
    private async Task<List<PrivilegedUser>> CompletePrivilegedUsersAsync(
        string token, KnightEntraIdConfiguration cfg, List<PrivilegedUser> users, CancellationToken ct)
    {
        var pending = users.Where(u => u.LastSignIn is null || u.ViaGroups is not null).Select(u => u.Id).ToList();
        if (pending.Count == 0) return users;
        var byId = users.ToDictionary(u => u.Id, StringComparer.OrdinalIgnoreCase);
        var withActivity = true;
        foreach (var batch in pending.Chunk(MaxIdsPerUserFilter))
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await foreach (var u in _graph.GetPagedAsync(token, cfg, PrivilegedUsersUrl(batch, withActivity), ct))
                    {
                        if (Str(u, "id") is not { } id || !byId.TryGetValue(id, out var known)) continue;
                        byId[id] = known with
                        {
                            LastSignIn = known.LastSignIn ?? LastSignIn(u),
                            DisplayName = known.DisplayName ?? Str(u, "displayName"),
                            UserPrincipalName = known.UserPrincipalName ?? Str(u, "userPrincipalName"),
                            IsGuest = known.ViaGroups is null ? known.IsGuest : string.Equals(Str(u, "userType"), "Guest", StringComparison.OrdinalIgnoreCase),
                        };
                    }
                    break;
                }
                catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, ct) && withActivity && attempt == 0)
                {
                    _log?.LogDebug("Atividade das contas privilegiadas não lida pelo filtro de usuários; nova tentativa sem signInActivity.");
                    withActivity = false;
                }
                catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, ct))
                {
                    // Nem nome nem tipo: as contas ficam como vieram (atividade ausente → controle não avaliado, com o motivo).
                    _log?.LogDebug("Contas privilegiadas via grupo não completadas pelo filtro de usuários.");
                    return users.Select(u => byId[u.Id]).ToList();
                }
            }
        }
        return users.Select(u => byId[u.Id]).ToList();
    }

    /// <summary>A capacidade: grupos das políticas HABILITADAS (inclusão e exclusão) somados aos grupos dos papéis.</summary>
    private async Task CollectGroupMembershipAsync(
        GroupReader groups, PrivilegedAccumulator privileged, DirectoryConfigurationBox box, List<KnightCapabilityStatus> caps, EntraRun run)
    {
        var started = _time.GetTimestamp();
        if (box.Policies is null && !privileged.Collected)
        {
            // Depende das políticas e do inventário de papéis: sem nenhum dos dois não há grupo a ler — não é falha própria.
            caps.Add(new KnightCapabilityStatus(KnightCapability.EntraGroupMembership, KnightCapabilityOutcome.NotAttempted,
                "Depende das políticas de acesso condicional e do inventário de papéis privilegiados, que não foram coletados nesta execução."));
            return;
        }
        try
        {
            foreach (var p in (box.Policies ?? Array.Empty<ConditionalAccessPolicyConfiguration>())
                         .Where(p => p.State == ConditionalAccessPolicyState.Enabled))
            {
                foreach (var g in p.IncludeGroups) await groups.ReadAsync(g, null, new[] { $"policy:{p.Id}:include" }, run.Ct);
                foreach (var g in p.ExcludeGroups) await groups.ReadAsync(g, null, new[] { $"policy:{p.Id}:exclude" }, run.Ct);
            }

            var reads = groups.All.ToList();
            var failed = reads.Where(r => r.State != EntraGroupMembershipState.Complete).ToList();
            var detail = reads.Count == 0
                ? "Nenhum grupo referenciado pelas políticas habilitadas nem atribuído a papel privilegiado."
                : $"{reads.Count - failed.Count} de {reads.Count} grupo(s) lido(s) por completo"
                  + (failed.Count == 0 ? "." : "; " + string.Join("; ", failed.Take(5).Select(r => $"{groups.Describe(r.GroupId)}: {r.Detail}"))
                      + (failed.Count > 5 ? $"; e mais {failed.Count - 5}." : "."));

            // Todos recusados pelo mesmo motivo: o desfecho da capacidade é o motivo (e a análise trata todo grupo como
            // desconhecido, como antes). Havendo algum lido, a capacidade conclui e cada grupo guarda o próprio desfecho.
            if (reads.Count > 0 && failed.Count == reads.Count && failed.All(r => r.State == EntraGroupMembershipState.Denied))
            {
                box.Groups = null;
                caps.Add(new KnightCapabilityStatus(KnightCapability.EntraGroupMembership, KnightCapabilityOutcome.InsufficientPermission,
                    KnightRuleTextEntra.Trim(detail, 480),
                    new KnightCapabilityDiagnostic("read", KnightFailureKind.HttpStatus, Elapsed(started), failed[0].HttpStatus, failed[0].Code,
                        "/v1.0/groups/{id}/transitiveMembers", run.CorrelationId)));
                return;
            }

            box.Groups = groups.ToContracts();
            caps.Add(new KnightCapabilityStatus(KnightCapability.EntraGroupMembership, KnightCapabilityOutcome.Collected,
                KnightRuleTextEntra.Trim(detail, 480),
                failed.Count == 0 ? null : KnightFailureClassifier.Incomplete("read", Elapsed(started), run.CorrelationId, "GroupsNotFullyRead")));
        }
        catch (Exception ex) when (KnightFailureClassifier.IsCallerCancellation(ex, run.Ct))
        {
            throw;
        }
        catch (Exception ex)
        {
            box.Groups = null;
            var failure = KnightFailureClassifier.Classify(ex, "Microsoft Graph", "read", Elapsed(started), run.CorrelationId);
            LogFailure(KnightCapability.EntraGroupMembership.ToString(), failure, run);
            caps.Add(new KnightCapabilityStatus(KnightCapability.EntraGroupMembership, failure.Outcome, failure.Reason, failure.Diagnostic));
        }
    }

    /// <summary>A MESMA régua que a releitura do ADM usa (contas habilitadas da enumeração completa da MFA por usuário).</summary>
    private static IReadOnlyList<string>? IdentityKnightBoundaryAccounts(List<KnightConfigurationDocument> docs, List<KnightCapabilityStatus> caps) =>
        IdentityKnightBoundary.EnabledAccountIds(new KnightTenantConfiguration(docs, caps));

    /// <summary>Sinais do acesso condicional com políticas, papéis, pertencimento e contagem de contas desta coleta.</summary>
    private void AddConditionalAccessObservations(DirectoryConfigurationBox box, List<KnightObservation> obs, EntraRun run)
    {
        if (box.Policies is null) return; // a capacidade falhou: os sinais já ficaram ausentes com o motivo dela
        try
        {
            var analysis = ConditionalAccessAnalyzer.Analyze(
                new KnightDirectoryConfiguration(box.Policies, box.Roles, box.Groups, box.EnabledAccounts));
            obs.AddRange(ConditionalAccessAnalyzer.ToObservations(analysis, null));
        }
        catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, run.Ct))
        {
            var failure = KnightFailureClassifier.Classify(ex, "Microsoft Graph", "analysis", null, run.CorrelationId);
            LogFailure(KnightCapability.ConditionalAccessPolicies.ToString(), failure, run);
            MarkMissing(obs, new[] { KnightSignalKey.LegacyAuthenticationBlocked, KnightSignalKey.PrivilegedRolesWithoutMfaPolicy,
                KnightSignalKey.BaselineMfaPolicies }, failure.Reason);
        }
    }

    private static class KnightRuleTextEntra
    {
        public static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
