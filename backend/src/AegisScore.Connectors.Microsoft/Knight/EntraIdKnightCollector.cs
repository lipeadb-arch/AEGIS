using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Identity;
using AegisScore.Application.Knight;
using AegisScore.Domain;

namespace AegisScore.Connectors.Microsoft.Knight;

/// <summary>
/// Coletor REAL do Microsoft Entra ID (somente leitura) para o AEGIS KNIGHT. Autentica por client credentials
/// (por tenant, segredo recebido DECIFRADO no contexto — nunca persistido/logado aqui), consulta o Microsoft
/// Graph (paginado, com resiliência no HttpClient) e NORMALIZA as respostas em fatos tipados. Cada capacidade
/// tem sua permissão: um 403 vira <see cref="KnightCapabilityOutcome.InsufficientPermission"/> e os sinais
/// daquela capacidade ficam Missing (→ NotEvaluated na avaliação — NUNCA "Conforme"). Falha parcial não
/// invalida o assessment (estado PartialCollection); falha total do Graph NÃO cai para Demo — devolve o estado
/// real (AuthenticationFailure/Throttled/Unavailable). Erros são SANITIZADOS (sem token, segredo ou PII).
///
/// Permissões de aplicativo (Application) SOMENTE LEITURA usadas:
///   • Directory.Read.All   → papéis privilegiados e membros; concessões (appRoleAssignedTo) e consentimentos
///                            delegados (oauth2PermissionGrants);
///   • AuditLog.Read.All    → detalhes de registro de MFA e atividade de sign-in (cobertura de MFA, obsoletas);
///   • User.Read.All        → convidados e sua atividade;
///   • Policy.Read.All      → acesso condicional e security defaults (legada bloqueada, MFA admin, baseline);
///   • Application.Read.All → aplicações (credenciais vencendo) e service principals (permissões concedidas).
///
/// [AEGIS-MVP-MICROSOFT-COVERAGE-03] Permissões NOVAS deste pacote (risco de identidade — Microsoft Entra ID
/// Protection), cada uma destravando UMA capacidade INDEPENDENTE:
///   • IdentityRiskyUser.Read.All  → GET /v1.0/identityProtection/riskyUsers      (inventário agregado);
///   • IdentityRiskEvent.Read.All  → GET /v1.0/identityProtection/riskDetections  (detecções na janela).
/// Licença: a API de riskDetections EXIGE Microsoft Entra ID P1 ou P2 (documentação oficial); riskyUsers
/// exige P2; e com P1 as detecções PREMIUM chegam com riskEventType = "generic" — o evento existe, a
/// categoria é suprimida. Nada disso vira coleção vazia: permissão ausente e limitação de licença são
/// ESTADOS TIPADOS distintos (InsufficientPermission × LimitedByLicense).
///
/// [DECISÃO EXPLÍCITA] UserAuthenticationMethod.Read.All NÃO é usada nem exigida por este pacote. Iterar
/// GET /users/{id}/authentication/methods sobre toda a população para auditoria seria um N+1 (uma chamada por
/// usuário), ampliaria a exposição de dados pessoais e o custo operacional sem ganho para uma visão agregada —
/// e a Microsoft não recomenda esse uso. A ampliação da postura de métodos é feita no relatório AGREGADO já
/// autorizado por AuditLog.Read.All (reports/authenticationMethods/userRegistrationDetails), apenas
/// acrescentando campos ao $select da MESMA consulta paginada: zero chamadas adicionais, zero permissão nova.
///
/// Sobre "concedido × solicitado": AK-ENTRA-010 mede permissões de aplicativo EFETIVAMENTE CONCEDIDAS
/// (appRoleAssignments no service principal do Graph) — não <c>requiredResourceAccess</c>, que é apenas o que a
/// aplicação DECLARA/solicita. AK-ENTRA-013 conta consentimentos DELEGADOS tenant-wide (consentType=AllPrincipals).
/// </summary>
public sealed class EntraIdKnightCollector : IKnightCollector
{
    private const string Label = "Microsoft Entra ID";
    private const string MicrosoftGraphAppId = "00000003-0000-0000-c000-000000000000";

    private static readonly HashSet<string> HighPrivilegeGraphAppRoleIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "19dbc75e-c2e2-444c-a770-ec69d8559fc7",
        "9e3f62cf-ca93-4989-b6ce-bf83c28f9fe8",
        "1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9",
        "06b708a9-e830-4db3-a914-8e69da51d44f",
        "62a82d76-70ea-41e2-9197-370581804d09",
    };

    // ---- [AEGIS-MVP-MICROSOFT-COVERAGE-03] Endpoints e permissões do risco de identidade ----------------

    /// <summary>Permissão de aplicativo que destrava o inventário agregado de usuários em risco.</summary>
    internal const string RiskyUsersPermission = "IdentityRiskyUser.Read.All";

    /// <summary>Permissão de aplicativo que destrava as detecções/eventos de risco.</summary>
    internal const string RiskDetectionsPermission = "IdentityRiskEvent.Read.All";

    /// <summary>
    /// Inventário de usuários em risco. $select PEDE APENAS o que é agregável e NÃO pessoal — userDisplayName
    /// e userPrincipalName ficam DE FORA do próprio pedido (minimização na origem: o PII nem trafega).
    /// $top=500 é o MÁXIMO documentado por página; a continuação é sempre por @odata.nextLink validado.
    /// Nota oficial: riskyUsers e riskDetections suportam somente $filter e $select — NÃO há $orderby, e a
    /// janela temporal por isso é aplicada em CÓDIGO, com relógio injetado, e não numa URL não confirmada.
    /// </summary>
    internal const string RiskyUsersUrl =
        "identityProtection/riskyUsers?$select=isDeleted,isProcessing,riskLastUpdatedDateTime,riskLevel,riskState&$top=500";

    /// <summary>
    /// Detecções de risco. O $select EXCLUI deliberadamente ipAddress, location, requestId, correlationId,
    /// additionalInfo, userId, userDisplayName e userPrincipalName — nada disso é sequer solicitado.
    /// </summary>
    internal const string RiskDetectionsUrl =
        "identityProtection/riskDetections?$select=riskEventType,riskState,riskLevel,detectionTimingType,detectedDateTime,activityDateTime&$top=500";

    /// <summary>
    /// Teto OPERACIONAL de itens por capacidade de risco. Atingi-lo produz coleta PARCIAL explícita
    /// (IsComplete=false) — jamais um total silenciosamente truncado apresentado como verdade.
    /// </summary>
    internal const int MaxRiskItems = 50_000;

    private readonly IEntraGraphClient _graph;
    private readonly ILogger<EntraIdKnightCollector>? _log;
    private readonly TimeProvider _time;

    public EntraIdKnightCollector(
        IEntraGraphClient graph,
        ILogger<EntraIdKnightCollector>? log = null,
        TimeProvider? time = null)
    {
        _graph = graph;
        _log = log;
        // Relógio INJETÁVEL: todas as janelas (7/30 dias, obsolescência, vencimento de credencial) derivam de
        // UMA referência temporal por coleta — nenhum DateTimeOffset.UtcNow espalhado por regra ou por teste.
        _time = time ?? TimeProvider.System;
    }

    public KnightSourceType Source => KnightSourceType.MicrosoftEntraId;

    public async Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default)
    {
        if (context.Configuration is not KnightEntraIdConfiguration cfg)
            return KnightCollectionResult.NotConfigured(Source, Label);

        string token;
        try
        {
            token = await _graph.AcquireTokenAsync(cfg, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (EntraGraphException ex)
        {
            var state = ex.Kind switch
            {
                EntraGraphErrorKind.AuthFailure => KnightSourceState.AuthenticationFailure,
                EntraGraphErrorKind.Throttled => KnightSourceState.Throttled,
                _ => KnightSourceState.Unavailable,
            };
            return new KnightCollectionResult(Source, state, Label, KnightFactSet.Empty,
                Array.Empty<KnightCapabilityStatus>(), _time.GetUtcNow(),
                "Falha ao autenticar a aplicação junto ao Microsoft Graph.");
        }

        // UMA referência temporal por coleta lógica: janelas de 7/30 dias, obsolescência e vencimento de
        // credencial passam a ser reprodutíveis (e testáveis) a partir do MESMO instante.
        var now = _time.GetUtcNow();

        var obs = new List<KnightObservation>();
        var caps = new List<KnightCapabilityStatus>();
        var privileged = new PrivilegedAccumulator();
        var authPostureBox = new AuthenticationPostureBox();

        await RunCapabilityAsync(KnightCapability.PrivilegedRoleInventory,
            new[] { KnightSignalKey.PrivilegedAccountsTotal, KnightSignalKey.PrivilegedAccountsWithMailbox,
                    KnightSignalKey.StalePrivilegedAccounts, KnightSignalKey.ExternalMembersInPrivilegedRoles },
            () => CollectPrivilegedRolesAsync(token, cfg, privileged, obs, now, ct), obs, caps);

        await RunCapabilityAsync(KnightCapability.MfaRegistration,
            new[] { KnightSignalKey.MfaRegistrationCoveragePercent, KnightSignalKey.PrivilegedAccountsWithoutMfa },
            () => CollectMfaRegistrationAsync(token, cfg, privileged, obs, authPostureBox, ct), obs, caps);

        await RunCapabilityAsync(KnightCapability.GuestAccounts,
            new[] { KnightSignalKey.InactiveGuestAccounts },
            () => CollectGuestsAsync(token, cfg, obs, now, ct), obs, caps);

        await RunCapabilityAsync(KnightCapability.ConditionalAccessPolicies,
            new[] { KnightSignalKey.LegacyAuthenticationBlocked, KnightSignalKey.AdminMfaPolicyEnforced },
            () => CollectConditionalAccessAsync(token, cfg, obs, ct), obs, caps);

        await RunCapabilityAsync(KnightCapability.SecurityBaseline,
            new[] { KnightSignalKey.SecurityDefaultsEnabled },
            () => CollectSecurityDefaultsAsync(token, cfg, obs, ct), obs, caps);

        await RunCapabilityAsync(KnightCapability.ApplicationInventory,
            new[] { KnightSignalKey.ApplicationCredentialsExpiring },
            () => CollectApplicationCredentialsAsync(token, cfg, obs, now, ct), obs, caps);

        await RunCapabilityAsync(KnightCapability.ApplicationPermissions,
            new[] { KnightSignalKey.HighPrivilegeApplications },
            () => CollectApplicationPermissionsAsync(token, cfg, obs, ct), obs, caps);

        await RunCapabilityAsync(KnightCapability.ApplicationConsents,
            new[] { KnightSignalKey.AdminConsentedApplications },
            () => CollectDelegatedConsentsAsync(token, cfg, obs, ct), obs, caps);

        // ---- [AEGIS-MVP-MICROSOFT-COVERAGE-03] Risco de identidade: DUAS capacidades INDEPENDENTES -------
        // Rodam na MESMA operação lógica, com o MESMO token já adquirido acima — nunca uma segunda aquisição,
        // um segundo cliente Graph ou um pipeline paralelo. Cada uma classifica a própria falha e PRESERVA os
        // agregados que já conseguiu ler: um 403 numa NÃO apaga nem invalida a outra.
        var (riskyOutcome, riskyDetail, riskyFacts) = await CollectRiskyUsersAsync(token, cfg, ct);
        caps.Add(new KnightCapabilityStatus(KnightCapability.IdentityRiskyUsers, riskyOutcome, riskyDetail));

        var (detectionOutcome, detectionDetail, detectionFacts) = await CollectRiskDetectionsAsync(token, cfg, now, ct);
        caps.Add(new KnightCapabilityStatus(KnightCapability.IdentityRiskDetections, detectionOutcome, detectionDetail));

        var identityRisk = new IdentityRiskPosture(
            riskyOutcome, riskyDetail, riskyFacts,
            detectionOutcome, detectionDetail, detectionFacts,
            now);

        var facts = new KnightFactSet(obs);
        var runState = DeriveState(caps);
        return new KnightCollectionResult(
            Source, runState, Label, facts, caps, now, DescribeState(runState),
            identityRisk, authPostureBox.Value);
    }

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
        catch (EntraGraphException ex)
        {
            var (outcome, baseReason) = ex.Kind switch
            {
                EntraGraphErrorKind.InsufficientPermission => (KnightCapabilityOutcome.InsufficientPermission, "Permissão insuficiente para esta coleta."),
                EntraGraphErrorKind.Throttled => (KnightCapabilityOutcome.Throttled, "Throttling/limite de taxa do Microsoft Graph."),
                EntraGraphErrorKind.AuthFailure => (KnightCapabilityOutcome.AuthenticationFailure, "Falha de autenticação nesta coleta."),
                _ => (KnightCapabilityOutcome.Unavailable, "Microsoft Graph indisponível para esta coleta."),
            };
            var reason = BuildDiagnosticReason(baseReason, ex);
            _log?.LogWarning(
                "Falha Graph sanitizada em {Capability}: Outcome={Outcome}, HttpStatus={HttpStatus}, GraphCode={GraphCode}, Endpoint={Endpoint}",
                capability, outcome, ex.HttpStatusCode, ex.GraphErrorCode ?? "n/a", ex.EndpointPath ?? "n/a");
            caps.Add(new KnightCapabilityStatus(capability, outcome, reason));
            MarkMissing(obs, keys, reason);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Falha inesperada ao coletar a capacidade {Capability} do Entra.", capability);
            caps.Add(new KnightCapabilityStatus(capability, KnightCapabilityOutcome.Error, "Erro inesperado nesta coleta."));
            MarkMissing(obs, keys, "Erro inesperado nesta coleta.");
        }
    }

    private static string BuildDiagnosticReason(string baseReason, EntraGraphException ex)
    {
        var parts = new List<string>();
        if (ex.HttpStatusCode is { } status) parts.Add($"HTTP {status}");
        if (!string.IsNullOrWhiteSpace(ex.GraphErrorCode)) parts.Add($"Graph: {ex.GraphErrorCode}");
        if (!string.IsNullOrWhiteSpace(ex.EndpointPath)) parts.Add($"endpoint: {ex.EndpointPath}");
        return parts.Count == 0 ? baseReason : $"{baseReason} {string.Join(" · ", parts)}";
    }

    private static void MarkMissing(List<KnightObservation> obs, KnightSignalKey[] keys, string reason)
    {
        var already = obs.Select(o => o.Key).ToHashSet();
        foreach (var k in keys)
            if (already.Add(k))
                obs.Add(KnightObservation.MissingData(k, reason));
    }

    private async Task CollectPrivilegedRolesAsync(
        string token, KnightEntraIdConfiguration cfg, PrivilegedAccumulator acc, List<KnightObservation> obs,
        DateTimeOffset now, CancellationToken ct)
    {
        var members = new Dictionary<string, MemberInfo>(StringComparer.OrdinalIgnoreCase);

        await foreach (var role in _graph.GetPagedAsync(token, cfg, "directoryRoles?$select=id,displayName", ct))
        {
            var roleId = Str(role, "id");
            if (string.IsNullOrEmpty(roleId)) continue;
            var url = $"directoryRoles/{roleId}/members?$select=id,userType,signInActivity";
            await foreach (var m in _graph.GetPagedAsync(token, cfg, url, ct))
            {
                var id = Str(m, "id");
                if (string.IsNullOrEmpty(id)) continue;
                members[id] = new MemberInfo(ClassifyMember(m), Str(m, "userType"), LastSignIn(m));
            }
        }

        acc.Collected = true;
        acc.PrivilegedUsers = members
            .Where(kv => kv.Value.Kind == MemberKind.User)
            .Select(kv => new PrivilegedUser(kv.Key, kv.Value.LastSignIn))
            .ToList();
        acc.AllMembersClassifiable = members.Values.All(v => v.Kind is MemberKind.User or MemberKind.ServicePrincipal);

        var total = members.Count;
        var external = members.Values.Count(v => string.Equals(v.UserType, "Guest", StringComparison.OrdinalIgnoreCase));

        obs.Add(KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, total));
        obs.Add(KnightObservation.OfCount(KnightSignalKey.ExternalMembersInPrivilegedRoles, external));
        obs.Add(KnightObservation.MissingData(KnightSignalKey.PrivilegedAccountsWithMailbox,
            "A propriedade de diretório (mail) não comprova mailbox Exchange ativa; não avaliado nesta coleta."));

        if (!acc.AllMembersClassifiable)
        {
            obs.Add(KnightObservation.MissingData(KnightSignalKey.StalePrivilegedAccounts,
                "Há membro de papel privilegiado não identificável como usuário — cobertura de atividade incompleta."));
        }
        else if (acc.PrivilegedUsers.Any(u => u.LastSignIn is null))
        {
            obs.Add(KnightObservation.MissingData(KnightSignalKey.StalePrivilegedAccounts,
                "Atividade de sign-in ausente para parte das contas privilegiadas — não avaliado (requer AuditLog.Read.All e licença compatível)."));
        }
        else
        {
            var cutoff = now.AddDays(-KnightCatalog.StalePrivilegedWindowDays);
            var stale = acc.PrivilegedUsers.Count(u => u.LastSignIn < cutoff);
            obs.Add(KnightObservation.OfCount(KnightSignalKey.StalePrivilegedAccounts, stale));
        }
    }

    /// <summary>
    /// Cobertura de registro de MFA a partir do relatório AGREGADO <c>userRegistrationDetails</c>
    /// (AuditLog.Read.All, já concedida). [AEGIS-MVP-MICROSOFT-COVERAGE-03] O <c>$select</c> foi AMPLIADO com
    /// <c>isPasswordlessCapable</c> e <c>methodsRegistered</c> na MESMA consulta paginada — nenhuma chamada
    /// extra, nenhuma permissão nova e NENHUMA iteração por usuário em
    /// <c>/users/{id}/authentication/methods</c> (que seria N+1 e exporia dados pessoais sem necessidade).
    /// Os agregados derivados alimentam a postura de métodos; os SINAIS avaliáveis do KNIGHT permanecem
    /// exatamente os mesmos de antes.
    /// </summary>
    private async Task CollectMfaRegistrationAsync(
        string token, KnightEntraIdConfiguration cfg, PrivilegedAccumulator acc, List<KnightObservation> obs,
        AuthenticationPostureBox authBox, CancellationToken ct)
    {
        var total = 0;
        var mfaCapable = 0;
        var mfaRegistered = 0;
        var passwordlessCapable = 0;
        var capabilityUnknown = 0;
        var malformed = false;
        var noMfaIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var methods = new IdentityRiskAccumulator();

        var url = "reports/authenticationMethods/userRegistrationDetails"
            + "?$select=id,isMfaCapable,isMfaRegistered,isPasswordlessCapable,methodsRegistered";

        // Uma falha de página intermediária aqui é propagada (a capacidade inteira vira Missing/NotEvaluated,
        // como sempre foi) — a postura agregada só é publicada quando a leitura termina de fato.
        await foreach (var u in _graph.GetPagedAsync(token, cfg, url, ct))
        {
            total++;
            var id = Str(u, "id");
            if (!string.IsNullOrEmpty(id)) seenIds.Add(id);

            if (Bool(u, "isMfaRegistered") == true) mfaRegistered++;
            if (Bool(u, "isPasswordlessCapable") == true) passwordlessCapable++;
            foreach (var m in ArrayStrings(u, "methodsRegistered"))
                methods.Add(IdentityRiskLevel.Unknown, IdentityRiskState.Unknown, null, m);

            var capable = Bool(u, "isMfaCapable") ?? Bool(u, "isMfaRegistered");
            if (capable is null) { malformed = true; capabilityUnknown++; continue; }
            if (capable.Value) mfaCapable++;
            else if (!string.IsNullOrEmpty(id)) noMfaIds.Add(id);
        }

        // Postura AGREGADA de métodos: publicada mesmo quando os SINAIS ficam Missing (ex.: registro malformado),
        // porque total/registrados/passwordless continuam sendo fatos honestos sobre o que foi lido.
        authBox.Value = new IdentityAuthenticationPosture(
            TotalUsers: total,
            MfaCapable: mfaCapable,
            MfaRegistered: mfaRegistered,
            PasswordlessCapable: passwordlessCapable,
            CapabilityUnknown: capabilityUnknown,
            MethodsRegistered: methods.TopCategories(IdentityRiskWindows.TopDetectionTypes),
            IsComplete: true);

        if (total == 0)
        {
            obs.Add(KnightObservation.MissingData(KnightSignalKey.MfaRegistrationCoveragePercent,
                "Relatório userRegistrationDetails vazio — cobertura de MFA não pode ser afirmada (denominador zero)."));
            obs.Add(KnightObservation.MissingData(KnightSignalKey.PrivilegedAccountsWithoutMfa,
                "Sem dados de registro de MFA para o cruzamento com contas privilegiadas."));
            return;
        }

        if (malformed)
        {
            obs.Add(KnightObservation.MissingData(KnightSignalKey.MfaRegistrationCoveragePercent,
                "Registro de MFA com dados malformados (isMfaCapable ausente/inválido) — cobertura não avaliada."));
            obs.Add(KnightObservation.MissingData(KnightSignalKey.PrivilegedAccountsWithoutMfa,
                "Registro de MFA malformado impede o cruzamento com contas privilegiadas."));
            return;
        }

        var pct = Math.Round(100.0 * mfaCapable / total, 1);
        obs.Add(KnightObservation.OfRatio(KnightSignalKey.MfaRegistrationCoveragePercent, pct));

        if (!acc.Collected)
        {
            obs.Add(KnightObservation.MissingData(KnightSignalKey.PrivilegedAccountsWithoutMfa,
                "Inventário de papéis privilegiados indisponível para o cruzamento com MFA."));
        }
        else if (!acc.AllMembersClassifiable)
        {
            obs.Add(KnightObservation.MissingData(KnightSignalKey.PrivilegedAccountsWithoutMfa,
                "Há membro de papel privilegiado não identificável como usuário — cruzamento com MFA incompleto."));
        }
        else if (acc.PrivilegedUsers.Any(u => !seenIds.Contains(u.Id)))
        {
            obs.Add(KnightObservation.MissingData(KnightSignalKey.PrivilegedAccountsWithoutMfa,
                "Há conta(s) privilegiada(s) ausente(s) do relatório de registro de MFA — cobertura incompleta (ausência não implica MFA configurada)."));
        }
        else
        {
            var privWithout = acc.PrivilegedUsers.Count(u => noMfaIds.Contains(u.Id));
            obs.Add(KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithoutMfa, privWithout));
        }
    }

    private async Task CollectGuestsAsync(
        string token, KnightEntraIdConfiguration cfg, List<KnightObservation> obs, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-KnightCatalog.InactiveGuestWindowDays);
        var inactive = 0;
        var url = "users?$filter=userType eq 'Guest'&$select=id,signInActivity,createdDateTime&$top=999";
        await foreach (var g in _graph.GetPagedAsync(token, cfg, url, ct))
        {
            var last = LastSignIn(g) ?? Date(g, "createdDateTime");
            if (last is null || last < cutoff) inactive++;
        }
        obs.Add(KnightObservation.OfCount(KnightSignalKey.InactiveGuestAccounts, inactive));
    }

    private async Task CollectConditionalAccessAsync(
        string token, KnightEntraIdConfiguration cfg, List<KnightObservation> obs, CancellationToken ct)
    {
        var legacyBlocked = false;
        var adminMfa = false;
        await foreach (var p in _graph.GetPagedAsync(token, cfg, "identity/conditionalAccess/policies", ct))
        {
            if (!string.Equals(Str(p, "state"), "enabled", StringComparison.OrdinalIgnoreCase)) continue;

            var controls = BuiltInControls(p);
            var cond = Obj(p, "conditions");
            var users = Obj(cond, "users");
            var apps = Obj(cond, "applications");

            var appliesAllApps = ArrayStrings(apps, "includeApplications")
                .Any(a => a.Equals("All", StringComparison.OrdinalIgnoreCase));
            var appliesAllUsers = ArrayStrings(users, "includeUsers")
                .Any(u => u.Equals("All", StringComparison.OrdinalIgnoreCase));
            var hasExclusions = ArrayStrings(users, "excludeUsers").Count > 0
                || ArrayStrings(users, "excludeGroups").Count > 0
                || ArrayStrings(users, "excludeRoles").Count > 0;
            var targetsLegacy = ArrayStrings(cond, "clientAppTypes").Any(c =>
                c.Equals("exchangeActiveSync", StringComparison.OrdinalIgnoreCase) || c.Equals("other", StringComparison.OrdinalIgnoreCase));

            if (targetsLegacy && controls.Contains("block") && appliesAllApps && appliesAllUsers && !hasExclusions)
                legacyBlocked = true;

            var requiresMfa = controls.Contains("mfa");
            if (requiresMfa && appliesAllUsers && appliesAllApps && !hasExclusions)
                adminMfa = true;
        }
        obs.Add(KnightObservation.OfFlag(KnightSignalKey.LegacyAuthenticationBlocked, legacyBlocked));
        obs.Add(KnightObservation.OfFlag(KnightSignalKey.AdminMfaPolicyEnforced, adminMfa));
    }

    private async Task CollectSecurityDefaultsAsync(
        string token, KnightEntraIdConfiguration cfg, List<KnightObservation> obs, CancellationToken ct)
    {
        var root = await _graph.GetJsonAsync(token, cfg, "policies/identitySecurityDefaultsEnforcementPolicy", ct);
        var enabled = Bool(root, "isEnabled") ?? false;
        obs.Add(KnightObservation.OfFlag(KnightSignalKey.SecurityDefaultsEnabled, enabled));
    }

    private async Task CollectApplicationCredentialsAsync(
        string token, KnightEntraIdConfiguration cfg, List<KnightObservation> obs, DateTimeOffset now, CancellationToken ct)
    {
        var window = now.AddDays(KnightCatalog.AppCredentialExpiryWindowDays);
        var expiring = 0;
        var url = "applications?$select=id,displayName,passwordCredentials,keyCredentials&$top=999";
        await foreach (var app in _graph.GetPagedAsync(token, cfg, url, ct))
        {
            if (HasExpiringCredential(app, "passwordCredentials", window) || HasExpiringCredential(app, "keyCredentials", window))
                expiring++;
        }
        obs.Add(KnightObservation.OfCount(KnightSignalKey.ApplicationCredentialsExpiring, expiring));
    }

    private async Task CollectApplicationPermissionsAsync(
        string token, KnightEntraIdConfiguration cfg, List<KnightObservation> obs, CancellationToken ct)
    {
        var sp = await _graph.GetJsonAsync(token, cfg, $"servicePrincipals(appId='{MicrosoftGraphAppId}')?$select=id", ct);
        var graphSpId = Str(sp, "id");
        if (string.IsNullOrEmpty(graphSpId))
        {
            obs.Add(KnightObservation.MissingData(KnightSignalKey.HighPrivilegeApplications,
                "Service principal do Microsoft Graph não localizado para ler as concessões de permissão."));
            return;
        }

        var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var url = $"servicePrincipals/{graphSpId}/appRoleAssignedTo?$select=principalId,principalType,appRoleId&$top=999";
        await foreach (var a in _graph.GetPagedAsync(token, cfg, url, ct))
        {
            if (!string.Equals(Str(a, "principalType"), "ServicePrincipal", StringComparison.OrdinalIgnoreCase)) continue;
            var appRoleId = Str(a, "appRoleId");
            var principalId = Str(a, "principalId");
            if (appRoleId is not null && principalId is not null && HighPrivilegeGraphAppRoleIds.Contains(appRoleId))
                apps.Add(principalId);
        }
        obs.Add(KnightObservation.OfCount(KnightSignalKey.HighPrivilegeApplications, apps.Count));
    }

    private async Task CollectDelegatedConsentsAsync(
        string token, KnightEntraIdConfiguration cfg, List<KnightObservation> obs, CancellationToken ct)
    {
        var clients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var g in _graph.GetPagedAsync(token, cfg, "oauth2PermissionGrants?$select=clientId,consentType&$top=999", ct))
        {
            if (!string.Equals(Str(g, "consentType"), "AllPrincipals", StringComparison.OrdinalIgnoreCase)) continue;
            var clientId = Str(g, "clientId");
            if (!string.IsNullOrEmpty(clientId)) clients.Add(clientId);
        }
        obs.Add(KnightObservation.OfCount(KnightSignalKey.AdminConsentedApplications, clients.Count));
    }

    // ---- [AEGIS-MVP-MICROSOFT-COVERAGE-03] Risco de identidade (Entra ID Protection) -------------------

    /// <summary>
    /// Inventário AGREGADO de usuários em risco (<c>GET /v1.0/identityProtection/riskyUsers</c>,
    /// <c>IdentityRiskyUser.Read.All</c>). NADA pessoal é solicitado nem normalizado: o resultado são
    /// contagens e distribuições. Uma falha em página INTERMEDIÁRIA preserva o que já foi lido e devolve
    /// <c>IsComplete=false</c> — o número vira um PISO explícito, nunca um zero forjado.
    /// </summary>
    private async Task<(KnightCapabilityOutcome Outcome, string? Detail, IdentityRiskyUserFacts? Facts)>
        CollectRiskyUsersAsync(string token, KnightEntraIdConfiguration cfg, CancellationToken ct)
    {
        var acc = new IdentityRiskAccumulator();
        long total = 0, deleted = 0, processing = 0;
        var complete = false;
        KnightCapabilityOutcome outcome;
        string? detail = null;

        try
        {
            await foreach (var u in _graph.GetPagedAsync(token, cfg, RiskyUsersUrl, ct))
            {
                if (total >= MaxRiskItems) break;   // teto operacional: sai com IsComplete=false, sem truncar em silêncio
                total++;

                // Usuário já EXCLUÍDO do diretório: contado à parte e FORA das distribuições/KPIs ativos —
                // uma conta removida não "exige investigação", mas também não some do relato.
                if (Bool(u, "isDeleted") == true) { deleted++; continue; }
                if (Bool(u, "isProcessing") == true) processing++;

                acc.Add(
                    IdentityRiskVocabulary.LevelOf(Str(u, "riskLevel")),
                    IdentityRiskVocabulary.StateOf(Str(u, "riskState")),
                    Date(u, "riskLastUpdatedDateTime"));
            }

            complete = total < MaxRiskItems;
            outcome = KnightCapabilityOutcome.Collected;
            if (!complete)
                detail = $"Leitura interrompida no teto operacional de {MaxRiskItems} registros — os números são um piso, não o total.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelamento do CHAMADOR: propaga — não é uma falha da fonte e não deve virar estado da capacidade.
            throw;
        }
        catch (OperationCanceledException)
        {
            // TIMEOUT do HttpClient (o token do chamador NÃO foi cancelado): é indisponibilidade desta
            // capacidade — a outra dimensão segue o seu curso, e o que já foi lido é preservado.
            outcome = KnightCapabilityOutcome.Unavailable;
            detail = "Tempo esgotado ao ler usuários em risco no Microsoft Graph."
                + (total > 0 ? " Leitura parcial preservada." : "");
            _log?.LogWarning("Timeout ao coletar {Capability} do Entra ID Protection.", KnightCapability.IdentityRiskyUsers);
        }
        catch (EntraGraphException ex)
        {
            outcome = ClassifyRiskFailure(ex);
            detail = DescribeRiskFailure(ex, outcome, "usuários em risco", RiskyUsersPermission, total > 0);
            LogRiskFailure(KnightCapability.IdentityRiskyUsers, outcome, ex);
        }
        catch (Exception ex)
        {
            outcome = KnightCapabilityOutcome.Error;
            detail = "Erro inesperado ao ler os usuários em risco.";
            _log?.LogWarning(ex, "Falha inesperada ao coletar usuários em risco do Entra ID Protection.");
        }

        // Sem NENHUM registro lido não existe fato: devolvemos null (a UI dirá "não coletado"), jamais zeros.
        if (total == 0 && outcome != KnightCapabilityOutcome.Collected)
            return (outcome, detail, null);

        var facts = new IdentityRiskyUserFacts(
            Total: total,
            Deleted: deleted,
            Processing: processing,
            Levels: acc.Levels,
            States: acc.States,
            HighRiskActive: acc.HighRiskActive,
            MostRecentRiskUpdateAt: acc.MostRecent,
            IsComplete: outcome == KnightCapabilityOutcome.Collected && complete);

        return (outcome, detail, facts);
    }

    /// <summary>
    /// Detecções de risco na janela DETERMINÍSTICA de <see cref="IdentityRiskWindows.DetectionWindowDays"/>
    /// dias (<c>GET /v1.0/identityProtection/riskDetections</c>, <c>IdentityRiskEvent.Read.All</c>).
    ///
    /// A janela é aplicada em CÓDIGO a partir do relógio injetado: a documentação oficial confirma apenas
    /// <c>$filter</c> e <c>$select</c> nesse recurso, sem especificar filtro por <c>detectedDateTime</c> nem
    /// oferecer <c>$orderby</c> — fixar um filtro temporal não confirmado na URL arriscaria um 400 num tenant
    /// real e transformaria dado existente em "indisponível". Eventos fora da janela e eventos SEM carimbo de
    /// tempo são contados à parte, nunca descartados em silêncio.
    /// </summary>
    private async Task<(KnightCapabilityOutcome Outcome, string? Detail, IdentityRiskDetectionFacts? Facts)>
        CollectRiskDetectionsAsync(string token, KnightEntraIdConfiguration cfg, DateTimeOffset now, CancellationToken ct)
    {
        var windowStart = now.AddDays(-IdentityRiskWindows.DetectionWindowDays);
        var recentStart = now.AddDays(-IdentityRiskWindows.RecentDetectionWindowDays);

        var acc = new IdentityRiskAccumulator();
        long read = 0, outside = 0, undated = 0, recent = 0;
        long realtime = 0, nearRealtime = 0, offline = 0, notDefined = 0, unknownTiming = 0;
        var complete = false;
        KnightCapabilityOutcome outcome;
        string? detail = null;

        try
        {
            await foreach (var d in _graph.GetPagedAsync(token, cfg, RiskDetectionsUrl, ct))
            {
                if (read >= MaxRiskItems) break;
                read++;

                // detectedDateTime é o carimbo canônico; activityDateTime é o fallback documentado.
                var stamp = Date(d, "detectedDateTime") ?? Date(d, "activityDateTime");
                if (stamp is null) { undated++; continue; }

                // Sem teto superior de propósito: um carimbo à frente do relógio local (desalinhamento) ainda é
                // uma detecção RECENTE — escondê-la seria pior do que contá-la.
                if (stamp < windowStart) { outside++; continue; }
                if (stamp >= recentStart) recent++;

                switch (IdentityRiskVocabulary.TimingOf(Str(d, "detectionTimingType")))
                {
                    case IdentityRiskDetectionTiming.Realtime: realtime++; break;
                    case IdentityRiskDetectionTiming.NearRealtime: nearRealtime++; break;
                    case IdentityRiskDetectionTiming.Offline: offline++; break;
                    case IdentityRiskDetectionTiming.NotDefined: notDefined++; break;
                    default: unknownTiming++; break;
                }

                acc.Add(
                    IdentityRiskVocabulary.LevelOf(Str(d, "riskLevel")),
                    IdentityRiskVocabulary.StateOf(Str(d, "riskState")),
                    stamp,
                    Str(d, "riskEventType"));
            }

            complete = read < MaxRiskItems;
            outcome = KnightCapabilityOutcome.Collected;
            if (!complete)
                detail = $"Leitura interrompida no teto operacional de {MaxRiskItems} detecções — os números são um piso, não o total.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelamento do CHAMADOR: propaga — não é uma falha da fonte e não deve virar estado da capacidade.
            throw;
        }
        catch (OperationCanceledException)
        {
            // TIMEOUT do HttpClient (o token do chamador NÃO foi cancelado): é indisponibilidade desta
            // capacidade — a outra dimensão segue o seu curso, e o que já foi lido é preservado.
            outcome = KnightCapabilityOutcome.Unavailable;
            detail = "Tempo esgotado ao ler detecções de risco no Microsoft Graph."
                + (read > 0 ? " Leitura parcial preservada." : "");
            _log?.LogWarning("Timeout ao coletar {Capability} do Entra ID Protection.", KnightCapability.IdentityRiskDetections);
        }
        catch (EntraGraphException ex)
        {
            outcome = ClassifyRiskFailure(ex);
            detail = DescribeRiskFailure(ex, outcome, "detecções de risco", RiskDetectionsPermission, read > 0);
            LogRiskFailure(KnightCapability.IdentityRiskDetections, outcome, ex);
        }
        catch (Exception ex)
        {
            outcome = KnightCapabilityOutcome.Error;
            detail = "Erro inesperado ao ler as detecções de risco.";
            _log?.LogWarning(ex, "Falha inesperada ao coletar detecções de risco do Entra ID Protection.");
        }

        if (read == 0 && outcome != KnightCapabilityOutcome.Collected)
            return (outcome, detail, null);

        var facts = new IdentityRiskDetectionFacts(
            WindowDays: IdentityRiskWindows.DetectionWindowDays,
            WindowStart: windowStart,
            WindowEnd: now,
            TotalInWindow: acc.Total,
            OutsideWindow: outside,
            Undated: undated,
            InRecentWindow: recent,
            Levels: acc.Levels,
            States: acc.States,
            Realtime: realtime,
            NearRealtime: nearRealtime,
            Offline: offline,
            TimingNotDefined: notDefined,
            TimingUnknown: unknownTiming,
            PremiumDetailWithheld: acc.CategoryCount(IdentityRiskVocabulary.GenericPremiumType),
            HighRiskActive: acc.HighRiskActive,
            TopTypes: acc.TopCategories(IdentityRiskWindows.TopDetectionTypes),
            MostRecentDetectionAt: acc.MostRecent,
            IsComplete: outcome == KnightCapabilityOutcome.Collected && complete);

        return (outcome, detail, facts);
    }

    /// <summary>
    /// Traduz a falha do transporte no desfecho TIPADO da capacidade de risco. Licença e permissão são
    /// desfechos DIFERENTES: 403 sem menção a licença é consentimento ausente; um código que mencione licença/
    /// premium/assinatura — ou um 402/404 no recurso do Identity Protection, que o Graph devolve quando o tenant
    /// não tem o produto — é limitação de licença. Nenhum caminho devolve <c>Collected</c>: falha nunca vira dado.
    /// </summary>
    internal static KnightCapabilityOutcome ClassifyRiskFailure(EntraGraphException ex)
    {
        if (MentionsLicense(ex.GraphErrorCode)) return KnightCapabilityOutcome.LimitedByLicense;
        if (ex.HttpStatusCode is 402 or 404) return KnightCapabilityOutcome.LimitedByLicense;
        if (ex.Kind == EntraGraphErrorKind.InsufficientPermission || ex.HttpStatusCode == 403)
            return KnightCapabilityOutcome.InsufficientPermission;
        return ex.Kind switch
        {
            EntraGraphErrorKind.Throttled => KnightCapabilityOutcome.Throttled,
            EntraGraphErrorKind.AuthFailure => KnightCapabilityOutcome.AuthenticationFailure,
            _ => KnightCapabilityOutcome.Unavailable,
        };
    }

    private static bool MentionsLicense(string? graphErrorCode) =>
        !string.IsNullOrWhiteSpace(graphErrorCode)
        && (graphErrorCode!.Contains("license", StringComparison.OrdinalIgnoreCase)
            || graphErrorCode.Contains("premium", StringComparison.OrdinalIgnoreCase)
            || graphErrorCode.Contains("subscription", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Detalhe SANITIZADO da falha: só capacidade, permissão que a destrava, status HTTP e código do Graph já
    /// sanitizados pelo transporte. NUNCA token, segredo, URL com query string, corpo, PII ou mensagem bruta.
    /// </summary>
    private static string DescribeRiskFailure(
        EntraGraphException ex, KnightCapabilityOutcome outcome, string what, string permission, bool partial)
    {
        var head = outcome switch
        {
            KnightCapabilityOutcome.InsufficientPermission =>
                $"Sem permissão para ler {what}: conceda {permission} (consentimento de administrador).",
            KnightCapabilityOutcome.LimitedByLicense =>
                $"O tenant não tem licença Microsoft Entra ID P1/P2 suficiente para ler {what}.",
            KnightCapabilityOutcome.Throttled =>
                $"Throttling do Microsoft Graph ao ler {what}.",
            KnightCapabilityOutcome.AuthenticationFailure =>
                $"Falha de autenticação ao ler {what}.",
            _ => $"Microsoft Graph indisponível ao ler {what}.",
        };

        var parts = new List<string>();
        if (ex.HttpStatusCode is { } status) parts.Add($"HTTP {status.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(ex.GraphErrorCode)) parts.Add($"Graph: {ex.GraphErrorCode}");
        if (!string.IsNullOrWhiteSpace(ex.EndpointPath)) parts.Add($"endpoint: {ex.EndpointPath}");
        if (partial) parts.Add("leitura parcial preservada");

        return parts.Count == 0 ? head : $"{head} {string.Join(" · ", parts)}";
    }

    private void LogRiskFailure(KnightCapability capability, KnightCapabilityOutcome outcome, EntraGraphException ex) =>
        _log?.LogWarning(
            "Falha Graph sanitizada em {Capability}: Outcome={Outcome}, HttpStatus={HttpStatus}, GraphCode={GraphCode}, Endpoint={Endpoint}",
            capability, outcome, ex.HttpStatusCode, ex.GraphErrorCode ?? "n/a", ex.EndpointPath ?? "n/a");

    /// <summary>Caixa mutável que carrega a postura agregada de métodos para fora da capacidade de MFA.</summary>
    private sealed class AuthenticationPostureBox
    {
        public IdentityAuthenticationPosture? Value { get; set; }
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
        KnightSourceState.Completed => "Coleta do Microsoft Entra ID concluída.",
        KnightSourceState.PartialCollection => "Coleta parcial do Microsoft Entra ID — parte das capacidades faltou (permissão/indisponibilidade).",
        KnightSourceState.InsufficientPermission => "Permissões insuficientes para a coleta do Microsoft Entra ID.",
        KnightSourceState.AuthenticationFailure => "Falha de autenticação junto ao Microsoft Graph durante a coleta.",
        KnightSourceState.Throttled => "Throttling do Microsoft Graph durante a coleta.",
        KnightSourceState.Unavailable => "Microsoft Graph indisponível durante a coleta.",
        KnightSourceState.Error => "Erro inesperado durante a coleta do Microsoft Entra ID.",
        _ => "Coleta do Microsoft Entra ID.",
    };

    private enum MemberKind { User, ServicePrincipal, Other }
    private sealed record MemberInfo(MemberKind Kind, string? UserType, DateTimeOffset? LastSignIn);
    private sealed record PrivilegedUser(string Id, DateTimeOffset? LastSignIn);

    private sealed class PrivilegedAccumulator
    {
        public bool Collected;
        public IReadOnlyList<PrivilegedUser> PrivilegedUsers = Array.Empty<PrivilegedUser>();
        public bool AllMembersClassifiable = true;
    }

    private static MemberKind ClassifyMember(JsonElement m)
    {
        var odata = Str(m, "@odata.type");
        if (odata is not null)
        {
            if (odata.Equals("#microsoft.graph.user", StringComparison.OrdinalIgnoreCase)) return MemberKind.User;
            if (odata.Equals("#microsoft.graph.servicePrincipal", StringComparison.OrdinalIgnoreCase)) return MemberKind.ServicePrincipal;
            return MemberKind.Other;
        }
        return Str(m, "userType") is not null ? MemberKind.User : MemberKind.Other;
    }

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => (bool?)null }
            : null;

    private static JsonElement Obj(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) ? v : default;

    private static DateTimeOffset? Date(JsonElement e, string prop)
    {
        var s = Str(e, prop);
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    private static DateTimeOffset? LastSignIn(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("signInActivity", out var a) || a.ValueKind != JsonValueKind.Object)
            return null;
        return Date(a, "lastSignInDateTime") ?? Date(a, "lastNonInteractiveSignInDateTime");
    }

    private static IReadOnlyList<string> BuiltInControls(JsonElement policy) =>
        policy.TryGetProperty("grantControls", out var gc) && gc.ValueKind == JsonValueKind.Object
            ? ArrayStrings(gc, "builtInControls").Select(s => s.ToLowerInvariant()).ToList()
            : Array.Empty<string>();

    private static IReadOnlyList<string> ArrayStrings(JsonElement parent, string prop)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var it in arr.EnumerateArray())
            if (it.ValueKind == JsonValueKind.String && it.GetString() is { } s) list.Add(s);
        return list;
    }

    private static bool HasExpiringCredential(JsonElement app, string prop, DateTimeOffset window)
    {
        if (!app.TryGetProperty(prop, out var creds) || creds.ValueKind != JsonValueKind.Array) return false;
        foreach (var c in creds.EnumerateArray())
        {
            var end = Date(c, "endDateTime");
            if (end is not null && end <= window) return true;
        }
        return false;
    }
}
