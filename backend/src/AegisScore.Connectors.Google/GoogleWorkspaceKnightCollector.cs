using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Knight;
using AegisScore.Domain;

namespace AegisScore.Connectors.Google;

/// <summary>
/// Coletor REAL do Google Workspace (SOMENTE LEITURA) para o AEGIS KNIGHT. Autentica por service account com
/// domain-wide delegation (JSON DECIFRADO em memória, nunca persistido/logado aqui), consulta o Admin SDK
/// Directory e a Reports API (paginado por <c>nextPageToken</c>, com teto fail-closed) e NORMALIZA as respostas
/// em fatos tipados. Cada capacidade tem sua permissão: um 403 vira
/// <see cref="KnightCapabilityOutcome.InsufficientPermission"/> e os sinais daquela capacidade ficam Missing
/// (→ NotEvaluated — NUNCA "Conforme"). Falha parcial não invalida o assessment (PartialCollection); falha total
/// NÃO cai para Demo — devolve o estado real. Erros são SANITIZADOS (sem token, chave, JSON ou PII).
///
/// COLETA APENAS METADADOS ADMINISTRATIVOS/CONFIGURAÇÃO/AUDITORIA. NÃO acessa conteúdo de Gmail, Drive ou Chat;
/// NÃO persiste listas de e-mails/usuários/eventos — apenas CONTAGENS e evidências sanitizadas.
///
/// APIs/escopos (somente leitura): Admin SDK Directory (admin.directory.user.readonly,
/// admin.directory.group.readonly, admin.directory.group.member.readonly, admin.directory.domain.readonly) e
/// Reports API (admin.reports.audit.readonly).
/// </summary>
public sealed class GoogleWorkspaceKnightCollector : IKnightCollector
{
    private const string Label = "Google Workspace";
    private static readonly DateTimeOffset Epoch = DateTimeOffset.FromUnixTimeSeconds(0);

    // Valores de visibilidade do Drive (Reports) que indicam exposição EXTERNA/pública. Domínio interno fica de fora.
    private static readonly HashSet<string> ExternalVisibilityValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "people_with_link", "public_on_the_web", "public", "shared_externally", "anyone_with_link",
    };

    private readonly IGoogleWorkspaceAuthenticator _auth;
    private readonly IGoogleWorkspaceApiClient _api;
    private readonly ILogger<GoogleWorkspaceKnightCollector>? _log;

    public GoogleWorkspaceKnightCollector(
        IGoogleWorkspaceAuthenticator auth, IGoogleWorkspaceApiClient api, ILogger<GoogleWorkspaceKnightCollector>? log = null)
    {
        _auth = auth;
        _api = api;
        _log = log;
    }

    public KnightSourceType Source => KnightSourceType.GoogleWorkspace;

    public async Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default)
    {
        if (context.Configuration is not KnightGoogleWorkspaceConfiguration cfg)
            return KnightCollectionResult.NotConfigured(Source, Label);

        string token;
        try
        {
            token = await _auth.AcquireAccessTokenAsync(cfg, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (GoogleWorkspaceException ex)
        {
            var state = ex.Kind switch
            {
                GoogleWorkspaceErrorKind.AuthFailure => KnightSourceState.AuthenticationFailure,
                GoogleWorkspaceErrorKind.Throttled => KnightSourceState.Throttled,
                _ => KnightSourceState.Unavailable,
            };
            return new KnightCollectionResult(Source, state, Label, KnightFactSet.Empty,
                Array.Empty<KnightCapabilityStatus>(), DateTimeOffset.UtcNow,
                "Falha ao autenticar a service account junto ao Google Workspace.");
        }

        var obs = new List<KnightObservation>();
        var caps = new List<KnightCapabilityStatus>();

        await RunCapabilityAsync(KnightCapability.DirectoryUsers,
            new[] { KnightSignalKey.TwoStepVerificationCoveragePercent, KnightSignalKey.SuperAdminsTotal,
                    KnightSignalKey.SuperAdminsWithout2Sv, KnightSignalKey.StaleSuperAdmins },
            () => CollectUsersAsync(token, cfg, obs, ct), obs, caps);

        await RunCapabilityAsync(KnightCapability.DirectoryGroups,
            new[] { KnightSignalKey.ExternalGroupMembers },
            () => CollectExternalGroupMembersAsync(token, cfg, obs, ct), obs, caps);

        await RunCapabilityAsync(KnightCapability.DriveSharingAudit,
            new[] { KnightSignalKey.ExternalDriveSharingEvents },
            () => CollectDriveExternalSharingAsync(token, obs, ct), obs, caps);

        await RunCapabilityAsync(KnightCapability.OAuthTokenAudit,
            new[] { KnightSignalKey.RecentOAuthGrants },
            () => CollectOAuthGrantsAsync(token, obs, ct), obs, caps);

        var facts = new KnightFactSet(obs);
        var runState = DeriveState(caps);
        return new KnightCollectionResult(Source, runState, Label, facts, caps, DateTimeOffset.UtcNow, DescribeState(runState));
    }

    // ---- Capacidades -------------------------------------------------------------------------------

    private async Task CollectUsersAsync(
        string token, KnightGoogleWorkspaceConfiguration cfg, List<KnightObservation> obs, CancellationToken ct)
    {
        var active = 0;
        var enrolled = 0;
        var superTotal = 0;
        var superWithout2Sv = 0;
        var superStale = 0;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-KnightCatalog.StalePrivilegedWindowDays);

        // Só os campos MÍNIMOS necessários para as contagens — sem e-mail/nome (privacidade).
        var path = $"admin/directory/v1/users?customer={Uri.EscapeDataString(cfg.CustomerId)}&maxResults=500&projection=basic" +
                   "&fields=nextPageToken,users(isAdmin,isEnrolledIn2Sv,isEnforcedIn2Sv,suspended,archived,lastLoginTime)";
        await foreach (var u in _api.GetPagedAsync(token, path, "users", ct))
        {
            if ((Bool(u, "suspended") ?? false) || (Bool(u, "archived") ?? false)) continue;   // só contas ativas
            active++;
            var enrolled2Sv = Bool(u, "isEnrolledIn2Sv") ?? false;
            if (enrolled2Sv) enrolled++;

            if (Bool(u, "isAdmin") == true)
            {
                superTotal++;
                if (!enrolled2Sv) superWithout2Sv++;
                var last = Date(u, "lastLoginTime");
                if (last is null || last <= Epoch || last < cutoff) superStale++;   // epoch (1970) = nunca logou
            }
        }

        // A paginação é FAIL-CLOSED (lança ao truncar); portanto uma enumeração concluída = listagem COMPLETA.
        // Denominador zero (nenhum usuário ativo) NÃO vira 100%: cobertura fica Missing.
        if (active == 0)
            obs.Add(KnightObservation.MissingData(KnightSignalKey.TwoStepVerificationCoveragePercent,
                "Diretório não retornou usuários ativos — cobertura de 2SV não pode ser afirmada (denominador zero)."));
        else
            obs.Add(KnightObservation.OfRatio(KnightSignalKey.TwoStepVerificationCoveragePercent,
                Math.Round(100.0 * enrolled / active, 1)));

        obs.Add(KnightObservation.OfCount(KnightSignalKey.SuperAdminsTotal, superTotal));
        obs.Add(KnightObservation.OfCount(KnightSignalKey.SuperAdminsWithout2Sv, superWithout2Sv));
        obs.Add(KnightObservation.OfCount(KnightSignalKey.StaleSuperAdmins, superStale));
    }

    private async Task CollectExternalGroupMembersAsync(
        string token, KnightGoogleWorkspaceConfiguration cfg, List<KnightObservation> obs, CancellationToken ct)
    {
        // Domínios da organização — para classificar "externo". Sem eles não há como afirmar externalidade.
        var domainsRoot = await _api.GetJsonAsync(token,
            $"admin/directory/v1/customer/{Uri.EscapeDataString(cfg.CustomerId)}/domains?fields=domains(domainName)", ct);
        var orgDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (domainsRoot.ValueKind == JsonValueKind.Object && domainsRoot.TryGetProperty("domains", out var darr) && darr.ValueKind == JsonValueKind.Array)
            foreach (var d in darr.EnumerateArray())
                if (Str(d, "domainName") is { Length: > 0 } dn) orgDomains.Add(dn);

        if (orgDomains.Count == 0)
        {
            obs.Add(KnightObservation.MissingData(KnightSignalKey.ExternalGroupMembers,
                "Domínios da organização indisponíveis — não é possível classificar membros externos."));
            return;
        }

        // Contamos e-mails externos DISTINTOS de forma transitória (só a CONTAGEM é persistida — nunca a lista).
        var external = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupsPath = $"admin/directory/v1/groups?customer={Uri.EscapeDataString(cfg.CustomerId)}&maxResults=200&fields=nextPageToken,groups(id)";
        await foreach (var g in _api.GetPagedAsync(token, groupsPath, "groups", ct))
        {
            var gid = Str(g, "id");
            if (string.IsNullOrEmpty(gid)) continue;
            var membersPath = $"admin/directory/v1/groups/{Uri.EscapeDataString(gid)}/members?maxResults=200&fields=nextPageToken,members(email,type)";
            await foreach (var m in _api.GetPagedAsync(token, membersPath, "members", ct))
            {
                if (!string.Equals(Str(m, "type"), "USER", StringComparison.OrdinalIgnoreCase)) continue;
                var email = Str(m, "email");
                var at = email?.LastIndexOf('@') ?? -1;
                if (email is null || at < 0 || at == email.Length - 1) continue;
                var domain = email[(at + 1)..];
                if (!orgDomains.Contains(domain)) external.Add(email);
            }
        }
        obs.Add(KnightObservation.OfCount(KnightSignalKey.ExternalGroupMembers, external.Count));
    }

    private async Task CollectDriveExternalSharingAsync(string token, List<KnightObservation> obs, CancellationToken ct)
    {
        var startTime = DateTimeOffset.UtcNow.AddDays(-KnightCatalog.GoogleAuditWindowDays).ToString("o", CultureInfo.InvariantCulture);
        var count = 0;
        var path = "admin/reports/v1/activity/users/all/applications/drive?maxResults=1000&eventName=change_document_visibility" +
                   $"&startTime={Uri.EscapeDataString(startTime)}";
        await foreach (var activity in _api.GetPagedAsync(token, path, "items", ct))
            if (IsExternalVisibilityChange(activity)) count++;
        obs.Add(KnightObservation.OfCount(KnightSignalKey.ExternalDriveSharingEvents, count));
    }

    private async Task CollectOAuthGrantsAsync(string token, List<KnightObservation> obs, CancellationToken ct)
    {
        var startTime = DateTimeOffset.UtcNow.AddDays(-KnightCatalog.GoogleAuditWindowDays).ToString("o", CultureInfo.InvariantCulture);
        var clients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // client_ids distintos (transitório)
        var path = "admin/reports/v1/activity/users/all/applications/token?maxResults=1000&eventName=authorize" +
                   $"&startTime={Uri.EscapeDataString(startTime)}";
        await foreach (var activity in _api.GetPagedAsync(token, path, "items", ct))
        {
            var clientId = FindEventParameter(activity, "client_id");
            if (!string.IsNullOrEmpty(clientId)) clients.Add(clientId);
        }
        obs.Add(KnightObservation.OfCount(KnightSignalKey.RecentOAuthGrants, clients.Count));
    }

    // ---- Execução resiliente de UMA capacidade (mesma semântica do coletor Entra) ------------------

    private async Task RunCapabilityAsync(
        KnightCapability capability, KnightSignalKey[] keys, Func<Task> collect,
        List<KnightObservation> obs, List<KnightCapabilityStatus> caps)
    {
        try
        {
            await collect();
            caps.Add(new KnightCapabilityStatus(capability, KnightCapabilityOutcome.Collected));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (GoogleWorkspaceException ex)
        {
            var (outcome, reason) = ex.Kind switch
            {
                GoogleWorkspaceErrorKind.InsufficientPermission => (KnightCapabilityOutcome.InsufficientPermission, "Permissão insuficiente para esta coleta."),
                GoogleWorkspaceErrorKind.Throttled => (KnightCapabilityOutcome.Throttled, "Throttling/limite de taxa do Google."),
                GoogleWorkspaceErrorKind.AuthFailure => (KnightCapabilityOutcome.AuthenticationFailure, "Falha de autenticação nesta coleta."),
                _ => (KnightCapabilityOutcome.Unavailable, "API do Google indisponível para esta coleta."),
            };
            caps.Add(new KnightCapabilityStatus(capability, outcome, reason));
            MarkMissing(obs, keys, reason);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Falha inesperada ao coletar a capacidade {Capability} do Google Workspace.", capability);
            caps.Add(new KnightCapabilityStatus(capability, KnightCapabilityOutcome.Error, "Erro inesperado nesta coleta."));
            MarkMissing(obs, keys, "Erro inesperado nesta coleta.");
        }
    }

    private static void MarkMissing(List<KnightObservation> obs, KnightSignalKey[] keys, string reason)
    {
        var already = obs.Select(o => o.Key).ToHashSet();
        foreach (var k in keys)
            if (already.Add(k))
                obs.Add(KnightObservation.MissingData(k, reason));
    }

    private static KnightSourceState DeriveState(IReadOnlyList<KnightCapabilityStatus> caps)
    {
        if (caps.Count == 0) return KnightSourceState.Unavailable;
        var collected = caps.Count(c => c.Outcome == KnightCapabilityOutcome.Collected);
        if (collected == caps.Count) return KnightSourceState.Completed;
        if (collected > 0) return KnightSourceState.PartialCollection;

        if (caps.All(c => c.Outcome == KnightCapabilityOutcome.InsufficientPermission)) return KnightSourceState.InsufficientPermission;
        if (caps.All(c => c.Outcome == KnightCapabilityOutcome.Throttled)) return KnightSourceState.Throttled;
        if (caps.All(c => c.Outcome == KnightCapabilityOutcome.AuthenticationFailure)) return KnightSourceState.AuthenticationFailure;
        if (caps.All(c => c.Outcome == KnightCapabilityOutcome.Error)) return KnightSourceState.Error;
        return KnightSourceState.Unavailable;
    }

    private static string DescribeState(KnightSourceState state) => state switch
    {
        KnightSourceState.Completed => "Coleta do Google Workspace concluída.",
        KnightSourceState.PartialCollection => "Coleta parcial do Google Workspace — parte das capacidades faltou (permissão/indisponibilidade).",
        KnightSourceState.InsufficientPermission => "Permissões insuficientes para a coleta do Google Workspace.",
        KnightSourceState.AuthenticationFailure => "Falha de autenticação junto ao Google Workspace durante a coleta.",
        KnightSourceState.Throttled => "Throttling do Google durante a coleta.",
        KnightSourceState.Unavailable => "API do Google indisponível durante a coleta.",
        KnightSourceState.Error => "Erro inesperado durante a coleta do Google Workspace.",
        _ => "Coleta do Google Workspace.",
    };

    // ---- Parsing seguro do JSON ---------------------------------------------------------------------

    /// <summary>Verdadeiro se a atividade do Drive tem um evento que muda a visibilidade para externo/público.</summary>
    private static bool IsExternalVisibilityChange(JsonElement activity)
    {
        if (activity.ValueKind != JsonValueKind.Object || !activity.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var ev in events.EnumerateArray())
        {
            if (ev.ValueKind != JsonValueKind.Object || !ev.TryGetProperty("parameters", out var pars) || pars.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var p in pars.EnumerateArray())
            {
                var name = Str(p, "name");
                if (name is null) continue;
                if (!name.Equals("visibility", StringComparison.OrdinalIgnoreCase) && !name.Equals("new_value", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (Str(p, "value") is { } v && ExternalVisibilityValues.Contains(v)) return true;
                if (p.TryGetProperty("multiValue", out var mv) && mv.ValueKind == JsonValueKind.Array)
                    foreach (var it in mv.EnumerateArray())
                        if (it.ValueKind == JsonValueKind.String && ExternalVisibilityValues.Contains(it.GetString()!)) return true;
            }
        }
        return false;
    }

    /// <summary>Extrai o valor (string) de um parâmetro nomeado no primeiro evento que o contém.</summary>
    private static string? FindEventParameter(JsonElement activity, string parameterName)
    {
        if (activity.ValueKind != JsonValueKind.Object || !activity.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var ev in events.EnumerateArray())
        {
            if (ev.ValueKind != JsonValueKind.Object || !ev.TryGetProperty("parameters", out var pars) || pars.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var p in pars.EnumerateArray())
                if (string.Equals(Str(p, "name"), parameterName, StringComparison.OrdinalIgnoreCase) && Str(p, "value") is { Length: > 0 } v)
                    return v;
        }
        return null;
    }

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => (bool?)null }
            : null;

    private static DateTimeOffset? Date(JsonElement e, string prop)
    {
        var s = Str(e, prop);
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }
}
