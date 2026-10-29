using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AegisScore.Application.Knight.Configuration;

// ============================================================================
//  [AEGIS-KNIGHT-MULTICLOUD-01] Leitura DETERMINÍSTICA das políticas de acesso condicional
// ============================================================================
// Antes, cada política era reduzida a duas flags, e a MFA administrativa só era reconhecida numa política que
// mirasse TODOS os usuários, TODAS as aplicações e não tivesse NENHUMA exclusão. Isso reprovava o desenho que
// a própria Microsoft recomenda (política dirigida aos papéis administrativos, com as contas de emergência
// excluídas) e, no sentido oposto, ignorava que uma política "somente relatório" não impõe nada.
//
// A leitura agora distingue, política a política:
//   • ESTADO — habilitada, desabilitada, somente relatório (só a primeira impõe);
//   • ALVOS — todos os usuários, papéis (por id de modelo), grupos, usuários nominais, e as exclusões de cada;
//   • APLICAÇÕES — todas, todas com exceções, ou um subconjunto;
//   • CONDIÇÕES que estreitam a exigência — localização, plataforma, risco, filtro de dispositivo, clientes;
//   • CONCESSÃO — MFA ou força de autenticação EXIGIDA, ou oferecida como ALTERNATIVA num "OU".
//
// Três coisas são mantidas SEPARADAS: a política EXISTIR, o ALVO que ela declara e a COBERTURA que os dados
// comprovam. A cobertura é lida MEMBRO A MEMBRO — é assim que o acesso condicional se aplica: uma política que
// inclui um papel vale para quem DETÉM o papel, e uma exclusão (por usuário, por papel, por grupo, de
// convidados) prevalece sobre qualquer inclusão. Para cada membro privilegiado conhecido:
//   • COBERTO — alguma política que impõe o alcança sem exclusão que o deixe de fora;
//   • NÃO RESOLVIDO — o alcance depende de GRUPO ou da condição de convidado, que esta entrega não coleta;
//   • EXCEÇÃO — as políticas que o alcançariam o excluem EXPLICITAMENTE, e nenhuma outra o cobre;
//   • NÃO ALCANÇADO — nenhuma política que impõe o tem como alvo.
//
// Uma exclusão explícita NÃO é irregular por si (é o padrão para contas de emergência), mas também NÃO comprova
// proteção nem controle compensatório — e a designação de contas de emergência não é inferível por API. Por
// isso ela nunca vira cobertura: um papel com exceções não é aprovado; fica sem conclusão, com as contas
// nomeadas. Já um membro NÃO ALCANÇADO, ou um papel em que NENHUM membro é coberto (todos excluídos, ou o
// próprio papel excluído), é lacuna comprovada. Outra política que comprovadamente cubra a exceção preserva a
// aprovação.
//
// A exigência por POLÍTICA não comprova que cada autenticação aplicou o segundo fator — isso seria
// autenticação observada, que esta entrega não coleta.

/// <summary>Leitura de UMA política: o que ela exige, de quem, onde e se de fato impõe.</summary>
public sealed record ConditionalAccessPolicyReading(
    ConditionalAccessPolicyConfiguration Policy,
    bool IsEnforced,
    bool RequiresMfa,
    bool MfaIsAlternative,
    bool Blocks,
    bool TargetsAllUsers,
    bool CoversAllApplications,
    IReadOnlyList<string> Narrowing,
    string Summary)
{
    /// <summary>A política declara alguma exclusão de usuários, grupos, papéis ou convidados.</summary>
    public bool HasExclusions =>
        Policy.ExcludeUsers.Count > 0 || Policy.ExcludeGroups.Count > 0 || Policy.ExcludeRoles.Count > 0
        || Policy.ExcludesGuestsOrExternalUsers;
}

/// <summary>Situação da exigência de MFA para UM papel privilegiado.</summary>
public enum RoleMfaCoverageState
{
    /// <summary>Todos os membros conhecidos são alcançados por política habilitada que exige MFA, sem exclusão.</summary>
    Covered = 0,

    /// <summary>
    /// Lacuna COMPROVADA: há membro que nenhuma política que impõe alcança, ou nenhum membro é coberto (todos
    /// excluídos, ou o próprio papel excluído).
    /// </summary>
    NotCovered = 1,

    /// <summary>A cobertura de algum membro depende de grupo/condição de convidado — pertencimento não coletado.</summary>
    Unresolved = 2,

    /// <summary>
    /// Há membros cobertos, e os demais estão EXCLUÍDOS explicitamente sem outra política que os cubra. Não é
    /// cobertura: a exceção não é irregular por si, mas também não comprova proteção.
    /// </summary>
    CoveredWithExceptions = 3,

    /// <summary>
    /// [AEGIS-KNIGHT-COLLECTION-GAPS-01] O papel só tem identidades de APLICAÇÃO: a exigência de MFA por política de usuários
    /// não se aplica a elas. O papel não entra na contagem (nem coberto, nem lacuna).
    /// </summary>
    NoUserMembers = 4,
}

/// <summary>Situação da exigência de MFA para UM membro privilegiado (usuário) de um papel.</summary>
public enum MemberMfaCoverageState
{
    Covered = 0,
    Excepted = 1,
    Unresolved = 2,
    NotTargeted = 3,
}

/// <summary>
/// Um membro e as políticas que decidem sua situação: as que o COBREM (<see cref="MemberMfaCoverageState.Covered"/>),
/// as que o EXCLUEM (<see cref="MemberMfaCoverageState.Excepted"/>) ou as que dependem de pertencimento não
/// coletado (<see cref="MemberMfaCoverageState.Unresolved"/>).
/// </summary>
public sealed record MemberMfaCoverage(string MemberId, MemberMfaCoverageState State, IReadOnlyList<string> PolicyIds);

/// <summary>Como UM papel privilegiado está coberto — membro a membro, com as políticas e as exceções.</summary>
public sealed record RoleMfaCoverage(
    DirectoryRoleConfiguration Role,
    RoleMfaCoverageState State,
    IReadOnlyList<string> CoveringPolicyIds,
    IReadOnlyList<MemberMfaCoverage> Members,
    IReadOnlyList<string> Notes)
{
    /// <summary>Membros excluídos explicitamente, sem outra política que os cubra.</summary>
    public IReadOnlyList<string> ExceptionMemberIds => IdsIn(MemberMfaCoverageState.Excepted);

    /// <summary>Membros que nenhuma política que impõe tem como alvo.</summary>
    public IReadOnlyList<string> UncoveredMemberIds => IdsIn(MemberMfaCoverageState.NotTargeted);

    /// <summary>Membros cuja cobertura depende de pertencimento não coletado.</summary>
    public IReadOnlyList<string> UnresolvedMemberIds => IdsIn(MemberMfaCoverageState.Unresolved);

    private IReadOnlyList<string> IdsIn(MemberMfaCoverageState state) =>
        Members.Where(m => m.State == state).Select(m => m.MemberId).ToList();
}

/// <summary>Conclusão sobre a exigência de MFA administrativa.</summary>
/// <param name="Evaluable">FALSE quando os dados não permitem afirmar nem negar (motivo em <paramref name="InconclusiveReason"/>).</param>
/// <param name="UncoveredRoleCount">Papéis com lacuna comprovada — o número que o indicador conta.</param>
/// <param name="Roles">Cobertura papel a papel (vazio quando o inventário de papéis não foi coletado).</param>
/// <param name="UniversalPolicyIds">Políticas que exigem MFA de TODOS os usuários, sem nenhuma exclusão.</param>
public sealed record AdminMfaConclusion(
    bool Evaluable,
    string? InconclusiveReason,
    int UncoveredRoleCount,
    IReadOnlyList<RoleMfaCoverage> Roles,
    IReadOnlyList<string> UniversalPolicyIds);

/// <summary>Conclusão sobre o bloqueio de autenticação legada.</summary>
/// <param name="Blocked">Bloqueio COMPROVADO para todos os usuários e aplicações (exceções cobertas por outra política).</param>
/// <param name="BlockingPolicyIds">Políticas que sustentam o bloqueio comprovado.</param>
/// <param name="PartialPolicyIds">Políticas que tocam a autenticação legada mas não contam (estado, alvo, aplicação, condição).</param>
/// <param name="ExceptionPolicyIds">Políticas que bloqueiam para todos os usuários, mas com exclusões que nenhuma outra política cobre.</param>
/// <param name="InconclusiveReason">Preenchido quando só há bloqueio com exceções: não se afirma nem se nega o bloqueio para todos.</param>
public sealed record LegacyAuthConclusion(
    bool Blocked,
    IReadOnlyList<string> BlockingPolicyIds,
    IReadOnlyList<string> PartialPolicyIds,
    IReadOnlyList<string> ExceptionPolicyIds,
    string? InconclusiveReason,
    IReadOnlyDictionary<string, string>? Reach = null);

/// <summary>Conclusão sobre a base mínima de exigência de MFA para o ambiente.</summary>
/// <param name="BaselinePolicyIds">Políticas habilitadas que exigem MFA com ALVO DECLARADO em todos os usuários, em todas as aplicações, sem condição que as estreite e sem exclusão de grupo.</param>
/// <param name="UnresolvedPolicyIds">Políticas que seriam base mínima, mas cujo alcance depende de grupo(s) não coletado(s).</param>
/// <param name="RestrictedPolicyIds">Políticas habilitadas que exigem MFA com alcance restrito (usuários, papéis, grupos, aplicações ou condições).</param>
/// <param name="InconclusiveReason">Preenchido quando não há base comprovada e o alcance da candidata depende de grupo.</param>
/// <param name="Reach">[AEGIS-KNIGHT-COLLECTION-GAPS-01] Alcance por conta das políticas dirigidas a grupo (ou com exclusão de
/// grupo) cujo pertencimento foi lido — o porquê de cada uma ser base, não base ou não resolvida.</param>
public sealed record MfaBaselineConclusion(
    IReadOnlyList<string> BaselinePolicyIds,
    IReadOnlyList<string> UnresolvedPolicyIds,
    IReadOnlyList<string> RestrictedPolicyIds,
    string? InconclusiveReason,
    IReadOnlyDictionary<string, string>? Reach = null);

/// <summary>
/// [AEGIS-KNIGHT-COLLECTION-GAPS-01] Índice do pertencimento lido: membro (listado), não membro (leitura COMPLETA sem ele) ou
/// desconhecido (grupo não lido, leitura parcial, recusada ou não encontrada). Sem pertencimento coletado (aquisições
/// anteriores), TODO grupo é desconhecido — a leitura de antes, intacta.
/// </summary>
public sealed class GroupMembershipIndex
{
    private readonly Dictionary<string, EntraGroupMembership>? _groups;

    // [AEGIS-KNIGHT-CA-GROUP-SCOPE-01] Contas de cada grupo em conjunto: a conferência conta a conta consulta o mesmo grupo
    // para cada conta habilitada de cada política.
    private readonly Dictionary<string, HashSet<string>> _users = new(StringComparer.OrdinalIgnoreCase);

    public GroupMembershipIndex(IReadOnlyList<EntraGroupMembership>? groups) =>
        _groups = groups?.GroupBy(g => g.GroupId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    public static GroupMembershipIndex None { get; } = new(null);

    /// <summary>O pertencimento foi coletado nesta aquisição (mesmo que algum grupo tenha falhado).</summary>
    public bool Collected => _groups is not null;

    public EntraGroupMembership? Get(string groupId) => _groups is not null && _groups.TryGetValue(groupId, out var g) ? g : null;

    /// <summary>Mesma leitura de <see cref="EntraGroupMembership.Holds"/>: listado, fora de leitura completa ou desconhecido.</summary>
    public bool? Holds(string groupId, string userId)
    {
        if (Get(groupId) is not { } g) return null;
        if (!_users.TryGetValue(groupId, out var users))
            _users[groupId] = users = g.UserIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return users.Contains(userId) ? true : g.IsComplete ? false : null;
    }

    public bool IsComplete(string groupId) => Get(groupId)?.IsComplete == true;

    public string Describe(string groupId) => Get(groupId) is { } g
        ? $"“{g.DisplayName ?? groupId}” ({EntraGroupMembership.Label(g.State)})"
        : $"“{groupId}” (pertencimento não lido)";
}

/// <summary>
/// [AEGIS-KNIGHT-CA-GROUP-SCOPE-01] O alcance das políticas sobre as contas com TUDO que a mesma coleta leu: papéis detidos
/// diretamente e via grupo, pertencimento dos grupos e a enumeração completa das contas habilitadas. A autenticação legada e as
/// exigências de acesso condicional usam este resolvedor, sobre o MESMO <c>ReachOf</c> da MFA administrativa e da base mínima —
/// uma só interpretação de grupo, papel e exclusão para todas as regras.
/// </summary>
internal sealed class CaScope
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    public CaScope(KnightDirectoryConfiguration directory)
    {
        Membership = new GroupMembershipIndex(directory.GroupMemberships);
        RolesKnown = directory.PrivilegedRoles is not null;
        (RolesOf, GroupRolesOf) = ConditionalAccessAnalyzer.RoleHoldings(
            directory.PrivilegedRoles ?? Array.Empty<DirectoryRoleConfiguration>(), Membership);
        Population = directory.EnabledAccountIds?.Distinct(Ci).ToList();
    }

    public GroupMembershipIndex Membership { get; }

    /// <summary>O inventário de papéis foi coletado (sem ele, inclusão/exclusão por papel não é conferível conta a conta).</summary>
    public bool RolesKnown { get; }

    /// <summary>Contas habilitadas da enumeração COMPLETA da coleta; nulo quando não disponível ou incompleta.</summary>
    public IReadOnlyList<string>? Population { get; }

    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> RolesOf { get; }
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> GroupRolesOf { get; }

    internal ConditionalAccessAnalyzer.Reach Of(ConditionalAccessPolicyReading r, string userId)
    {
        var direct = RolesOf.TryGetValue(userId, out var d) ? d : Array.Empty<string>();
        IReadOnlyCollection<string> viaGroup = GroupRolesOf.TryGetValue(userId, out var v)
            ? v.Where(t => !direct.Contains(t, Ci)).ToList()
            : Array.Empty<string>();
        return ConditionalAccessAnalyzer.ReachOf(r, userId, direct, viaGroup, Membership);
    }

    /// <summary>Alguma das políticas comprovadamente alcança a conta. Sem o inventário de papéis, exclusão por papel deixa incerto.</summary>
    public bool Covers(IEnumerable<ConditionalAccessPolicyReading> policies, string userId) =>
        policies.Any(o => (RolesKnown || o.Policy.ExcludeRoles.Count == 0) && Of(o, userId) == ConditionalAccessAnalyzer.Reach.Applies);

    /// <summary>Por que o alcance NÃO pode ser conferido conta a conta (nulo quando pode).</summary>
    public string? PopulationGap(IReadOnlyList<ConditionalAccessPolicyReading> policies) =>
        Population is null
            ? "sem a enumeração completa das contas habilitadas do diretório nesta coleta para conferir conta a conta"
            : Population.Count == 0
                ? "a enumeração desta coleta não retornou nenhuma conta habilitada — não há alcance a demonstrar"
                : !RolesKnown && policies.Any(r => r.Policy.IncludeRoles.Count > 0 || r.Policy.ExcludeRoles.Count > 0)
                    ? "há política dirigida a papéis, sem o inventário de papéis nesta coleta para conferir conta a conta"
                    : null;

    /// <summary>A leitura que falta para concluir: grupos não lidos por completo, condição de convidado, papel detido via grupo.</summary>
    public string MissingReadings(IReadOnlyList<ConditionalAccessPolicyReading> policies)
    {
        var parts = new List<string>();
        var open = policies.SelectMany(r => r.Policy.IncludeGroups.Concat(r.Policy.ExcludeGroups))
            .Distinct(Ci).Where(g => !Membership.IsComplete(g)).ToList();
        if (open.Count > 0) parts.Add("pertencimento não lido por completo de " + string.Join(", ", open.Select(Membership.Describe)));
        if (policies.Any(r => ConditionalAccessAnalyzer.IncludesGuests(r.Policy) || ConditionalAccessAnalyzer.ExcludesGuests(r.Policy)))
            parts.Add("a condição de convidado ou usuário externo");
        if (GroupRolesOf.Count > 0 && policies.Any(r => r.Policy.IncludeRoles.Count > 0 || r.Policy.ExcludeRoles.Count > 0))
            parts.Add("papel detido via grupo (a política dirigida ao papel não comprova alcançar quem o detém por grupo)");
        return parts.Count == 0 ? "leitura de alcance incompleta" : string.Join("; ", parts);
    }

    /// <summary>O alvo declarado da política, em palavras (grupos nomeados com o estado da leitura).</summary>
    public string Target(ConditionalAccessPolicyConfiguration p)
    {
        var parts = new List<string>();
        if (p.IncludeGroups.Count > 0) parts.Add(string.Join(", ", p.IncludeGroups.Select(Membership.Describe)));
        var users = p.IncludeUsers.Count(u => !u.Equals("All", StringComparison.OrdinalIgnoreCase) && !u.Equals("None", StringComparison.OrdinalIgnoreCase));
        if (users > 0) parts.Add(ConditionalAccessAnalyzer.Plural(users, "usuário nominal", "usuários nominais"));
        if (p.IncludeRoles.Count > 0) parts.Add(ConditionalAccessAnalyzer.Plural(p.IncludeRoles.Count, "papel", "papéis"));
        return parts.Count == 0 ? "alvo restrito" : string.Join(", ", parts);
    }
}

/// <summary>[AEGIS-KNIGHT-CA-GROUP-SCOPE-01] Desfecho da conferência conta a conta de um conjunto de políticas.</summary>
internal enum ScopedVerdict
{
    /// <summary>Cada conta habilitada é alcançada por alguma política.</summary>
    Covered,

    /// <summary>Há conta habilitada fora do alcance de todas — lacuna comprovada.</summary>
    Gap,

    /// <summary>Nenhuma conta é alcançada (todas excluídas explicitamente) — a exigência não protege ninguém.</summary>
    NoneReached,

    /// <summary>As demais alcançadas; há contas excluídas explicitamente sem outra política — não afirmado nem negado.</summary>
    Exceptions,

    /// <summary>Há conta de alcance desconhecido (grupo não lido por completo, convidado, papel via grupo).</summary>
    Unknown,

    /// <summary>A conferência não pôde ser feita (pertencimento ou enumeração ausente, população vazia).</summary>
    Unverifiable,
}

/// <param name="Detail">O porquê, com os números conta a conta (sem ponto final).</param>
/// <param name="CoveringPolicyIds">Políticas que alcançam ao menos uma conta (só em <see cref="ScopedVerdict.Covered"/>).</param>
/// <param name="Reach">Alcance de cada política sem alvo declarado em todos os usuários.</param>
internal sealed record ScopedCoverage(
    ScopedVerdict Verdict,
    string Detail,
    IReadOnlyList<string> CoveringPolicyIds,
    IReadOnlyList<string> ExceptedAccounts,
    IReadOnlyDictionary<string, string> Reach);

/// <summary>Resultado completo da leitura das políticas de uma coleta.</summary>
public sealed record ConditionalAccessAnalysis(
    IReadOnlyList<ConditionalAccessPolicyReading> Policies,
    AdminMfaConclusion AdminMfa,
    LegacyAuthConclusion LegacyAuth,
    MfaBaselineConclusion Baseline)
{
    public ConditionalAccessPolicyReading? Find(string policyId) =>
        Policies.FirstOrDefault(p => string.Equals(p.Policy.Id, policyId, StringComparison.Ordinal));
}

public static class ConditionalAccessAnalyzer
{
    /// <summary>Versão da REGRA de leitura — muda quando a interpretação das políticas muda.</summary>
    public const string RuleVersion = "aegis-ca-analysis-v1";

    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private const string GuestsValue = "GuestsOrExternalUsers";
    private const string RestrictedClients = "tipos de cliente restritos";

    /// <summary>
    /// Lê as políticas da coleta. Devolve <c>null</c> quando as políticas NÃO foram coletadas — ausência de
    /// dado nunca vira "nenhuma política".
    /// </summary>
    public static ConditionalAccessAnalysis? Analyze(KnightDirectoryConfiguration? configuration)
    {
        var policies = configuration?.ConditionalAccessPolicies;
        if (policies is null) return null;

        var readings = policies
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .Select(Read)
            .ToList();

        var directory = configuration!;
        var scope = new CaScope(directory);
        return new ConditionalAccessAnalysis(
            readings,
            ConcludeAdminMfa(readings, directory.PrivilegedRoles, scope.Membership),
            ConcludeLegacy(readings, scope),
            ConcludeBaseline(readings, scope.Membership, directory.PrivilegedRoles, directory.EnabledAccountIds));
    }

    // ---- Leitura de uma política ---------------------------------------------------------------------

    public static ConditionalAccessPolicyReading Read(ConditionalAccessPolicyConfiguration p)
    {
        var controls = p.BuiltInControls.Select(c => c.ToLowerInvariant()).ToList();
        var hasMfa = controls.Contains("mfa") || !string.IsNullOrWhiteSpace(p.AuthenticationStrengthId);
        var blocks = controls.Contains("block");

        // Quantas opções de concessão existem: num "OU", MFA é só uma alternativa se houver outras.
        var options = controls.Count(c => c != "block") + (string.IsNullOrWhiteSpace(p.AuthenticationStrengthId) ? 0 : 1);
        var isOr = string.Equals(p.GrantOperator, "OR", StringComparison.OrdinalIgnoreCase);
        var alternative = hasMfa && isOr && options > 1;
        var requiresMfa = hasMfa && !alternative;

        var allUsers = p.IncludeUsers.Contains("All", Ci);
        var allApps = p.IncludeApplications.Contains("All", Ci) && p.ExcludeApplications.Count == 0;

        var narrowing = new List<string>();
        if (p.HasLocationCondition) narrowing.Add("condição de localização");
        if (p.HasPlatformCondition) narrowing.Add("condição de plataforma");
        if (p.HasSignInRiskCondition) narrowing.Add("condição de risco de entrada");
        if (p.HasUserRiskCondition) narrowing.Add("condição de risco do usuário");
        if (p.HasDeviceFilter) narrowing.Add("filtro de dispositivo");
        if (NarrowsModernClients(p.ClientAppTypes)) narrowing.Add(RestrictedClients);

        return new ConditionalAccessPolicyReading(
            p, p.State == ConditionalAccessPolicyState.Enabled, requiresMfa, alternative, blocks,
            allUsers, allApps, narrowing, Describe(p, requiresMfa, alternative, blocks, narrowing));
    }

    /// <summary>
    /// Clientes modernos (navegador + aplicativos) fora do alcance estreitam a exigência. "all" ou lista vazia
    /// = todos os clientes. Clientes legados não fazem MFA de qualquer forma e têm indicador próprio.
    /// </summary>
    private static bool NarrowsModernClients(IReadOnlyList<string> clientAppTypes)
    {
        if (AllClientTypes(clientAppTypes)) return false;
        return !(clientAppTypes.Contains("browser", Ci) && clientAppTypes.Contains("mobileAppsAndDesktopClients", Ci));
    }

    /// <summary>"all" ou lista vazia (o padrão da fonte quando a condição não é configurada) = todos os tipos de cliente.</summary>
    private static bool AllClientTypes(IReadOnlyList<string> clientAppTypes) =>
        clientAppTypes.Count == 0 || clientAppTypes.Contains("all", Ci);

    /// <summary>A política alcança os DOIS canais legados: Exchange ActiveSync e "outros clientes" — ou todos os tipos.</summary>
    public static bool CoversAllLegacyClients(ConditionalAccessPolicyConfiguration p) =>
        AllClientTypes(p.ClientAppTypes)
        || (p.ClientAppTypes.Contains("exchangeActiveSync", Ci) && p.ClientAppTypes.Contains("other", Ci));

    /// <summary>A política alcança ao menos um canal legado (inclusive por abranger todos os tipos de cliente).</summary>
    public static bool TouchesLegacy(ConditionalAccessPolicyConfiguration p) =>
        AllClientTypes(p.ClientAppTypes)
        || p.ClientAppTypes.Contains("exchangeActiveSync", Ci) || p.ClientAppTypes.Contains("other", Ci)
        || p.ClientAppTypes.Contains("easSupported", Ci);

    internal static bool ExcludesGuests(ConditionalAccessPolicyConfiguration p) =>
        p.ExcludesGuestsOrExternalUsers || p.ExcludeUsers.Contains(GuestsValue, Ci);

    internal static bool IncludesGuests(ConditionalAccessPolicyConfiguration p) =>
        p.IncludesGuestsOrExternalUsers || p.IncludeUsers.Contains(GuestsValue, Ci);

    // ---- Alcance de uma política sobre UM usuário conhecido ------------------------------------------

    internal enum Reach { Applies, Excluded, NotTargeted, Unknown }

    /// <summary>
    /// A política vale para o usuário? Exclusões prevalecem sobre inclusões. <paramref name="userRoles"/> são os
    /// modelos dos papéis ATIVOS que o usuário detém (do inventário da mesma coleta). Grupo e condição de
    /// convidado não são resolvidos nesta entrega → <see cref="Reach.Unknown"/>, nunca suposição.
    /// <see cref="Reach.Excluded"/> só é devolvido quando a política o teria como alvo.
    /// </summary>
    internal static Reach ReachOf(ConditionalAccessPolicyReading r, string userId, IReadOnlyCollection<string> userRoles) =>
        ReachOf(r, userId, userRoles, Array.Empty<string>(), GroupMembershipIndex.None);

    /// <summary>
    /// [AEGIS-KNIGHT-COLLECTION-GAPS-01] O alcance com o pertencimento LIDO: o usuário listado num grupo incluído é alvo; listado
    /// num grupo excluído, excluído; ausente de um grupo lido por completo, fora dele. Grupo não lido, lido em parte, recusado
    /// ou não encontrado continua desconhecido para quem não está listado — nunca associação presumida, nunca exclusão
    /// ignorada. <paramref name="groupRoles"/> são os papéis que o usuário detém SÓ via grupo: a documentação do acesso
    /// condicional não afirma que a política dirigida ao papel alcance quem o detém por grupo, então esse caminho fica
    /// desconhecido (o grupo em si, quando é alvo, continua valendo pelo pertencimento).
    /// </summary>
    internal static Reach ReachOf(
        ConditionalAccessPolicyReading r, string userId, IReadOnlyCollection<string> userRoles,
        IReadOnlyCollection<string> groupRoles, GroupMembershipIndex membership)
    {
        var p = r.Policy;
        bool AnyGroup(IReadOnlyList<string> groups, bool? state) => groups.Any(g => membership.Holds(g, userId) == state);

        var targeted = r.TargetsAllUsers
            || p.IncludeUsers.Contains(userId, Ci)
            || p.IncludeRoles.Any(role => userRoles.Contains(role, Ci))
            || AnyGroup(p.IncludeGroups, true);
        var maybeTargeted = !targeted
            && (AnyGroup(p.IncludeGroups, null) || p.IncludeRoles.Any(role => groupRoles.Contains(role, Ci)) || IncludesGuests(p));

        var excluded = p.ExcludeUsers.Contains(userId, Ci) || p.ExcludeRoles.Any(role => userRoles.Contains(role, Ci))
            || AnyGroup(p.ExcludeGroups, true);
        if (excluded) return targeted ? Reach.Excluded : Reach.NotTargeted;
        if (!targeted && !maybeTargeted) return Reach.NotTargeted;

        var maybeExcluded = AnyGroup(p.ExcludeGroups, null) || p.ExcludeRoles.Any(role => groupRoles.Contains(role, Ci))
            || ExcludesGuests(p);
        return targeted && !maybeExcluded ? Reach.Applies : Reach.Unknown;
    }

    // ---- MFA administrativa -------------------------------------------------------------------------

    /// <summary>
    /// Só uma política HABILITADA, que EXIGE MFA, em TODAS as aplicações e sem condição que a estreite, conta
    /// como exigência. Somente relatório, desabilitada, "MFA ou outra coisa", aplicações parciais e condições de
    /// localização/risco são registradas como notas — nunca como cobertura.
    /// </summary>
    private static bool IsFullMfa(ConditionalAccessPolicyReading r) =>
        r.IsEnforced && r.RequiresMfa && r.CoversAllApplications && r.Narrowing.Count == 0;

    private static AdminMfaConclusion ConcludeAdminMfa(
        IReadOnlyList<ConditionalAccessPolicyReading> readings, IReadOnlyList<DirectoryRoleConfiguration>? roles,
        GroupMembershipIndex membership)
    {
        var full = readings.Where(IsFullMfa).ToList();

        var universal = full.Where(r => r.TargetsAllUsers && !r.HasExclusions).Select(r => r.Policy.Id).ToList();

        if (roles is null)
        {
            // Sem inventário de papéis não dá para dizer QUAIS papéis estão cobertos. Só uma política universal
            // SEM NENHUMA exclusão alcança todos eles por definição — com exclusão, o excluído pode ser um deles.
            if (universal.Count > 0)
                return new AdminMfaConclusion(true, null, 0, Array.Empty<RoleMfaCoverage>(), universal);
            var withExclusions = full.Any(r => r.TargetsAllUsers);
            return new AdminMfaConclusion(false,
                withExclusions
                    ? "O inventário de papéis privilegiados não foi coletado e a política que exige MFA de todos os "
                      + "usuários declara exclusões — sem o inventário não é possível saber se algum administrador está entre os excluídos."
                    : "O inventário de papéis privilegiados não foi coletado e nenhuma política exige MFA de todos os "
                      + "usuários — não é possível dizer quais papéis estão cobertos.",
                0, Array.Empty<RoleMfaCoverage>(), universal);
        }

        // Papéis que cada usuário DETÉM — a inclusão/exclusão por papel vale para quem tem o papel.
        var (rolesOf, groupRolesOf) = RoleHoldings(roles, membership);

        var coverages = new List<RoleMfaCoverage>();
        foreach (var role in roles
                     .Where(r => r.MemberCount > 0)
                     .OrderBy(r => r.DisplayName ?? r.TemplateId, StringComparer.OrdinalIgnoreCase))
        {
            coverages.Add(RoleCoverage(role, full, rolesOf, groupRolesOf, membership, CandidateNotes(role, readings, full)));
        }

        var uncovered = coverages.Count(c => c.State == RoleMfaCoverageState.NotCovered);
        var unresolved = coverages.Where(c => c.State == RoleMfaCoverageState.Unresolved).ToList();
        var excepted = coverages
            .Where(c => c.State == RoleMfaCoverageState.CoveredWithExceptions
                        || (c.State == RoleMfaCoverageState.Unresolved && c.ExceptionMemberIds.Count > 0))
            .ToList();

        // Um papel com lacuna comprovada é exposição, mesmo havendo outros não resolvidos ou com exceções.
        if (uncovered > 0)
            return new AdminMfaConclusion(true, null, uncovered, coverages, universal);

        if (unresolved.Count > 0 || excepted.Count > 0)
        {
            var parts = new List<string>();
            if (unresolved.Count > 0)
                parts.Add(UnresolvedRolesReason(unresolved.Count, membership.Collected));
            if (excepted.Count > 0)
                parts.Add(ExceptionsReason(excepted));
            return new AdminMfaConclusion(false, string.Join(" ", parts), 0, coverages, universal);
        }

        return new AdminMfaConclusion(true, null, 0, coverages, universal);
    }

    /// <summary>
    /// Por que papéis ficam sem conclusão. [AEGIS-KNIGHT-CA-GROUP-SCOPE-01] Compartilhado com as exigências de acesso condicional
    /// para papéis privilegiados: com o pertencimento lido, o texto diz QUAL leitura falta; sem ele, o texto de antes.
    /// </summary>
    internal static string UnresolvedRolesReason(int count, bool membershipCollected) => membershipCollected
        ? $"{Plural(count, "papel privilegiado tem", "papéis privilegiados têm")} cobertura não resolvida: "
          + "depende de grupo com pertencimento não lido por completo, da condição de convidado ou usuário externo, ou de "
          + "papel detido via grupo (a política dirigida ao papel não comprova alcançar quem o detém por grupo) — a cobertura "
          + "não pode ser afirmada nem negada; os membros e os grupos aparecem nomeados na evidência."
        : $"{Plural(count, "papel privilegiado depende", "papéis privilegiados dependem")} de "
          + "política direcionada a grupos ou a convidados, ou de exclusão de grupos, cujo pertencimento não é "
          + "coletado nesta entrega — a cobertura não pode ser afirmada nem negada.";

    /// <summary>
    /// Cobertura de UM papel pelas políticas que impõem a exigência (<paramref name="full"/>), membro a membro.
    /// [AEGIS-KNIGHT-CA-GROUP-SCOPE-01] Compartilhada com as exigências de acesso condicional para papéis privilegiados — quem
    /// detém o papel via grupo entra pelo pertencimento lido, como na MFA administrativa. <paramref name="candidateNotes"/>
    /// explica as políticas que tratam do tema mas não impõem (próprias de cada regra).
    /// </summary>
    internal static RoleMfaCoverage RoleCoverage(
        DirectoryRoleConfiguration role,
        IReadOnlyList<ConditionalAccessPolicyReading> full,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> rolesOf,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> groupRolesOf,
        GroupMembershipIndex membership,
        IReadOnlyList<string> candidateNotes) =>
        !role.MemberTypesCaptured
            ? role.UserMemberIds.Count == 0
                ? RoleLevelCoverage(role, full, candidateNotes)
                : MemberLevelCoverage(role, role.UserMemberIds, full, rolesOf, groupRolesOf, membership, candidateNotes)
            : TypedCoverage(role, full, rolesOf, groupRolesOf, membership, candidateNotes);

    /// <summary>
    /// Papéis que cada usuário detém DIRETAMENTE e, à parte, [AEGIS-KNIGHT-COLLECTION-GAPS-01] os que detém por pertencer a um
    /// grupo atribuído ao papel (pertencimento lido).
    /// </summary>
    internal static (Dictionary<string, IReadOnlyCollection<string>> Direct, Dictionary<string, IReadOnlyCollection<string>> ViaGroup) RoleHoldings(
        IReadOnlyList<DirectoryRoleConfiguration> roles, GroupMembershipIndex membership)
    {
        var direct = roles
            .SelectMany(role => role.UserMemberIds.Select(m => (m, role.TemplateId)))
            .GroupBy(x => x.m, Ci)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<string>)g.Select(x => x.TemplateId).ToHashSet(Ci), Ci);
        var viaGroup = roles
            .SelectMany(role => (role.GroupMemberIds ?? Array.Empty<string>())
                .SelectMany(g => membership.Get(g)?.UserIds ?? Array.Empty<string>())
                .Select(u => (u, role.TemplateId)))
            .GroupBy(x => x.u, Ci)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<string>)g.Select(x => x.TemplateId).ToHashSet(Ci), Ci);
        return (direct, viaGroup);
    }

    private static string ExceptionsReason(IReadOnlyList<RoleMfaCoverage> excepted)
    {
        var members = excepted.SelectMany(c => c.ExceptionMemberIds).Distinct(Ci).Count();
        var roleNames = string.Join(", ", excepted.Select(c => c.Role.DisplayName ?? c.Role.TemplateId));
        return $"{Plural(members, "conta privilegiada está excluída", "contas privilegiadas estão excluídas")} "
            + $"explicitamente das políticas que exigem MFA do papel, sem outra política que a(s) cubra ({roleNames}). "
            + "Uma exclusão não é irregular por si — é o padrão para contas de emergência —, mas não comprova proteção "
            + "nem controle compensatório, e a designação dessas contas não é verificável nesta coleta. O controle não é "
            + "aprovado nem reprovado; as contas aparecem nomeadas na evidência.";
    }

    /// <summary>
    /// [AEGIS-KNIGHT-COLLECTION-GAPS-01] Papel com membros por tipo (v2): os usuários diretos e os usuários dos grupos atribuídos
    /// ao papel são avaliados conta a conta; identidades de aplicação não são contas sujeitas à MFA de usuários; grupo com
    /// pertencimento não lido por completo e objeto de tipo não reconhecido deixam a cobertura NÃO RESOLVIDA (nunca coberta).
    /// </summary>
    private static RoleMfaCoverage TypedCoverage(
        DirectoryRoleConfiguration role,
        IReadOnlyList<ConditionalAccessPolicyReading> full,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> rolesOf,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> groupRolesOf,
        GroupMembershipIndex membership,
        IReadOnlyList<string> candidateNotes)
    {
        var groups = role.GroupMemberIds ?? Array.Empty<string>();
        var users = role.UserMemberIds
            .Concat(groups.SelectMany(g => membership.Get(g)?.UserIds ?? Array.Empty<string>()))
            .Distinct(Ci).ToList();
        var openGroups = groups.Where(g => !membership.IsComplete(g)).ToList();
        var others = role.OtherMemberIds ?? Array.Empty<string>();
        var apps = role.ServicePrincipalMemberIds ?? Array.Empty<string>();

        if (users.Count == 0 && openGroups.Count == 0 && others.Count == 0)
            return new RoleMfaCoverage(role, RoleMfaCoverageState.NoUserMembers, Array.Empty<string>(), Array.Empty<MemberMfaCoverage>(),
                new List<string>
                {
                    $"Só {Plural(apps.Count, "identidade de aplicação", "identidades de aplicação")} — a exigência de MFA por política de "
                    + "usuários não se aplica a elas; o papel não entra na contagem.",
                });

        var coverage = users.Count == 0
            ? new RoleMfaCoverage(role, RoleMfaCoverageState.Unresolved, Array.Empty<string>(), Array.Empty<MemberMfaCoverage>(),
                candidateNotes.ToList())
            : MemberLevelCoverage(role, users, full, rolesOf, groupRolesOf, membership, candidateNotes);

        var notes = coverage.Notes.ToList();
        if (groups.Count > 0)
            notes.Add("Detido via " + Plural(groups.Count, "grupo", "grupos") + ": " + string.Join(", ", groups.Select(membership.Describe)) + ".");
        if (apps.Count > 0)
            notes.Add($"{Plural(apps.Count, "identidade de aplicação", "identidades de aplicação")} no papel — fora da exigência de MFA de usuários.");
        if (others.Count > 0)
            notes.Add($"{Plural(others.Count, "membro de tipo não reconhecido", "membros de tipo não reconhecido")} na resposta da fonte.");

        // Um grupo não lido por completo ou um membro de tipo desconhecido impede afirmar cobertura — lacuna comprovada continua.
        var state = coverage.State;
        if ((openGroups.Count > 0 || others.Count > 0) && state is RoleMfaCoverageState.Covered or RoleMfaCoverageState.CoveredWithExceptions)
            state = RoleMfaCoverageState.Unresolved;
        return coverage with { State = state, Notes = notes };
    }

    private static RoleMfaCoverage MemberLevelCoverage(
        DirectoryRoleConfiguration role,
        IReadOnlyList<string> memberIds,
        IReadOnlyList<ConditionalAccessPolicyReading> full,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> rolesOf,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> groupRolesOf,
        GroupMembershipIndex membership,
        IReadOnlyList<string> candidateNotes)
    {
        var members = new List<MemberMfaCoverage>();
        var covering = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var memberId in memberIds.Distinct(Ci).OrderBy(m => m, StringComparer.Ordinal))
        {
            var direct = role.UserMemberIds.Contains(memberId, Ci);
            var userRoles = rolesOf.TryGetValue(memberId, out var held) ? held
                : direct ? new[] { role.TemplateId } : Array.Empty<string>();
            var viaGroups = groupRolesOf.TryGetValue(memberId, out var viaGroup)
                ? viaGroup.Where(t => !userRoles.Contains(t, Ci)).ToList()
                : new List<string>();
            var applies = new List<string>();
            var excludes = new List<string>();
            var unknown = new List<string>();
            foreach (var r in full)
            {
                switch (ReachOf(r, memberId, userRoles, viaGroups, membership))
                {
                    case Reach.Applies: applies.Add(r.Policy.Id); break;
                    case Reach.Excluded: excludes.Add(r.Policy.Id); break;
                    case Reach.Unknown: unknown.Add(r.Policy.Id); break;
                }
            }

            foreach (var id in applies) covering.Add(id);
            members.Add(applies.Count > 0 ? new MemberMfaCoverage(memberId, MemberMfaCoverageState.Covered, applies)
                : unknown.Count > 0 ? new MemberMfaCoverage(memberId, MemberMfaCoverageState.Unresolved, unknown)
                : excludes.Count > 0 ? new MemberMfaCoverage(memberId, MemberMfaCoverageState.Excepted, excludes)
                : new MemberMfaCoverage(memberId, MemberMfaCoverageState.NotTargeted, Array.Empty<string>()));
        }

        var notes = candidateNotes.ToList();
        foreach (var r in full.Where(r => r.Policy.ExcludeRoles.Contains(role.TemplateId, Ci)))
            notes.Insert(0, $"“{Name(r.Policy)}” exclui este papel.");

        int Count(MemberMfaCoverageState s) => members.Count(m => m.State == s);
        RoleMfaCoverageState state;
        if (Count(MemberMfaCoverageState.NotTargeted) > 0) state = RoleMfaCoverageState.NotCovered;
        else if (Count(MemberMfaCoverageState.Covered) == 0 && Count(MemberMfaCoverageState.Unresolved) == 0)
            state = RoleMfaCoverageState.NotCovered; // todos excluídos: a exigência não alcança o papel
        else if (Count(MemberMfaCoverageState.Unresolved) > 0) state = RoleMfaCoverageState.Unresolved;
        else if (Count(MemberMfaCoverageState.Excepted) > 0) state = RoleMfaCoverageState.CoveredWithExceptions;
        else state = RoleMfaCoverageState.Covered;

        if (state == RoleMfaCoverageState.Unresolved)
            notes.Add(membership.Collected
                ? "Parte da cobertura depende de grupo com pertencimento não lido por completo, da condição de convidado ou de papel detido via grupo."
                : "Parte da cobertura depende de grupo(s) ou da condição de convidado: o pertencimento não é coletado nesta entrega.");

        return new RoleMfaCoverage(role, state, covering.ToList(), members, notes);
    }

    /// <summary>
    /// Papel ativo sem membro USUÁRIO conhecido (só identidades de aplicação ou grupos atribuíveis): lido pelo
    /// alvo declarado ao papel, como antes — exclusão de grupo ou de convidados deixa a cobertura não resolvida.
    /// </summary>
    private static RoleMfaCoverage RoleLevelCoverage(
        DirectoryRoleConfiguration role,
        IReadOnlyList<ConditionalAccessPolicyReading> full,
        IReadOnlyList<string> candidateNotes)
    {
        var covering = new List<string>();
        var unknown = new List<string>();
        var notes = candidateNotes.ToList();
        foreach (var r in full)
        {
            var p = r.Policy;
            if (p.ExcludeRoles.Contains(role.TemplateId, Ci)) { notes.Insert(0, $"“{Name(p)}” exclui este papel."); continue; }
            var targeted = r.TargetsAllUsers || p.IncludeRoles.Contains(role.TemplateId, Ci);
            if (targeted && p.ExcludeGroups.Count == 0 && !ExcludesGuests(p)) covering.Add(p.Id);
            else if (targeted || p.IncludeGroups.Count > 0) unknown.Add(p.Id);
        }

        var state = covering.Count > 0 ? RoleMfaCoverageState.Covered
            : unknown.Count > 0 ? RoleMfaCoverageState.Unresolved
            : RoleMfaCoverageState.NotCovered;
        if (state == RoleMfaCoverageState.Unresolved)
            notes.Add("Política direcionada a grupo(s), ou com exclusão de grupo(s): o pertencimento não é coletado nesta entrega.");
        return new RoleMfaCoverage(role, state, state == RoleMfaCoverageState.Covered ? covering : Array.Empty<string>(),
            Array.Empty<MemberMfaCoverage>(), notes);
    }

    /// <summary>Por que as políticas que miram o papel e mencionam MFA NÃO contam como exigência.</summary>
    private static List<string> CandidateNotes(
        DirectoryRoleConfiguration role,
        IReadOnlyList<ConditionalAccessPolicyReading> readings,
        IReadOnlyList<ConditionalAccessPolicyReading> full)
    {
        var notes = new List<string>();
        foreach (var r in readings.Where(r => r.RequiresMfa || r.MfaIsAlternative))
        {
            var p = r.Policy;
            var targets = r.TargetsAllUsers || p.IncludeRoles.Contains(role.TemplateId, Ci)
                || p.IncludeUsers.Any(u => role.UserMemberIds.Contains(u, Ci));
            if (!targets || p.ExcludeRoles.Contains(role.TemplateId, Ci) || full.Contains(r)) continue;
            notes.Add($"“{Name(p)}” não conta como exigência: {WhyNotEnforcing(r)}.");
        }
        return notes;
    }

    private static string WhyNotEnforcing(ConditionalAccessPolicyReading r)
    {
        var reasons = new List<string>();
        if (r.Policy.State == ConditionalAccessPolicyState.ReportOnly) reasons.Add("está em somente relatório (não impõe)");
        else if (r.Policy.State == ConditionalAccessPolicyState.Disabled) reasons.Add("está desabilitada");
        else if (r.Policy.State == ConditionalAccessPolicyState.Unknown) reasons.Add("estado não reconhecido");
        if (r.MfaIsAlternative) reasons.Add("MFA é apenas uma alternativa (operador OU)");
        if (!r.CoversAllApplications) reasons.Add("não alcança todas as aplicações");
        if (r.Narrowing.Count > 0) reasons.Add("estreitada por " + string.Join(", ", r.Narrowing));
        return reasons.Count == 0 ? "alcance insuficiente" : string.Join("; ", reasons);
    }

    // ---- Autenticação legada -------------------------------------------------------------------------

    /// <summary>Bloqueio que alcança os dois canais legados, habilitado, em todas as aplicações e sem condição que o estreite.</summary>
    private static bool IsFullLegacyBlock(ConditionalAccessPolicyReading r) =>
        r.IsEnforced && r.Blocks && CoversAllLegacyClients(r.Policy) && r.CoversAllApplications
        && !r.Narrowing.Any(n => n != RestrictedClients);

    /// <remarks>
    /// [AEGIS-KNIGHT-CA-GROUP-SCOPE-01] Com o pertencimento lido: a exclusão por grupo lido por completo vira as contas excluídas,
    /// cada uma coberta (ou não) por outro bloqueio completo; e, sem bloqueio declarado para todos, os bloqueios dirigidos a grupo
    /// (somados aos demais bloqueios completos) são conferidos conta a conta sobre a enumeração completa das contas habilitadas.
    /// Grupo não lido por completo, enumeração ausente ou vazia, ou pertencimento não coletado deixam o bloqueio sem conclusão —
    /// nunca "não bloqueado" por falta de leitura.
    /// </remarks>
    private static LegacyAuthConclusion ConcludeLegacy(IReadOnlyList<ConditionalAccessPolicyReading> readings, CaScope scope)
    {
        var candidates = readings.Where(r => r.Blocks && TouchesLegacy(r.Policy)).ToList();
        var fullBlocks = candidates.Where(IsFullLegacyBlock).ToList();
        var forAll = fullBlocks.Where(r => r.TargetsAllUsers).ToList();

        var blocking = new List<string>();
        var withExceptions = new List<(ConditionalAccessPolicyReading Policy, IReadOnlyList<string> Open)>();
        foreach (var r in forAll)
        {
            var open = OpenExceptions(r, fullBlocks, scope, null);
            if (open.Count == 0)
            {
                blocking.Add(r.Policy.Id);
                // Quem cobre as exceções também sustenta o bloqueio.
                foreach (var id in CoveringLegacyPolicies(r, fullBlocks, scope))
                    if (!blocking.Contains(id)) blocking.Add(id);
            }
            else withExceptions.Add((r, open));
        }

        List<string> Partial() => candidates
            .Where(r => !blocking.Contains(r.Policy.Id) && withExceptions.All(w => !ReferenceEquals(w.Policy, r)))
            .Select(r => r.Policy.Id).ToList();

        if (blocking.Count > 0)
            return new LegacyAuthConclusion(true, blocking, Partial(), withExceptions.Select(w => w.Policy.Policy.Id).ToList(), null);

        if (withExceptions.Count > 0)
        {
            var reason = string.Join(" ", withExceptions.Select(w =>
                $"“{Name(w.Policy.Policy)}” bloqueia a autenticação legada em todas as aplicações para todos os usuários, "
                + "exceto " + string.Join(", ", w.Open) + ", e nenhuma outra política habilitada comprova o bloqueio para essas exceções."))
                + " Uma exclusão não é irregular por si, mas nela a autenticação legada continua possível; sem verificar as "
                + "exceções, o bloqueio para todos os usuários não pode ser afirmado nem negado.";
            return new LegacyAuthConclusion(false, blocking, Partial(), withExceptions.Select(w => w.Policy.Policy.Id).ToList(), reason);
        }

        // [AEGIS-KNIGHT-CA-GROUP-SCOPE-01] Sem bloqueio declarado para todos: os dirigidos a grupo, conferidos conta a conta.
        var grouped = fullBlocks.Where(r => r.Policy.IncludeGroups.Count > 0).ToList();
        if (grouped.Count == 0)
            return new LegacyAuthConclusion(false, blocking, Partial(), Array.Empty<string>(), null);

        var names = string.Join(", ", grouped.Select(r => "“" + Name(r.Policy) + "”"));
        var scoped = CoverageByAccount(fullBlocks, scope);
        switch (scoped.Verdict)
        {
            case ScopedVerdict.Covered:
                blocking.AddRange(scoped.CoveringPolicyIds);
                return new LegacyAuthConclusion(true, blocking, Partial(), Array.Empty<string>(), null, scoped.Reach);
            case ScopedVerdict.Gap:
            case ScopedVerdict.NoneReached:
                return new LegacyAuthConclusion(false, blocking, Partial(), Array.Empty<string>(), null, scoped.Reach);
            case ScopedVerdict.Exceptions:
                return new LegacyAuthConclusion(false, blocking, Partial(), grouped.Select(r => r.Policy.Id).ToList(),
                    $"Só política dirigida a grupo(s) bloqueia a autenticação legada ({names}): {scoped.Detail}. "
                    + "Uma exclusão não é irregular por si, mas nela a autenticação legada continua possível; o bloqueio para "
                    + "todos os usuários não pode ser afirmado nem negado.", scoped.Reach);
            default:
                return new LegacyAuthConclusion(false, blocking, Partial(), Array.Empty<string>(),
                    $"Só política dirigida a grupo(s) bloqueia a autenticação legada ({names}); {scoped.Detail} — o bloqueio para "
                    + "todos os usuários não pode ser afirmado nem negado.", scoped.Reach);
        }
    }

    /// <summary>
    /// Exclusões de uma política "para todos" que NENHUMA outra política que impõe a mesma exigência comprovadamente cobre,
    /// descritas em palavras. Usuário: coberto se outra política o alcança sem exclusão que o deixe de fora. Grupo, papel e
    /// convidados: cobertos por outra política que os inclua (ou inclua todos) SEM exclusões. Sem o inventário de papéis, uma
    /// exclusão por papel na outra política torna a cobertura do usuário incerta.
    /// [AEGIS-KNIGHT-CA-GROUP-SCOPE-01] Grupo excluído lido por completo: são as CONTAS dele, cada uma coberta ou não por outra
    /// política (inclusive dirigida a outro grupo lido); grupo não lido por completo continua exceção aberta, com a leitura que
    /// falta. Compartilhado pela autenticação legada e pelas exigências de acesso condicional (<paramref name="label"/> nomeia as
    /// contas; nulo = só a contagem).
    /// </summary>
    internal static List<string> OpenExceptions(
        ConditionalAccessPolicyReading policy,
        IReadOnlyList<ConditionalAccessPolicyReading> coverers,
        CaScope scope,
        Func<string, string>? label)
    {
        var others = coverers.Where(o => !ReferenceEquals(o, policy)).ToList();
        var p = policy.Policy;
        bool Structural(Func<ConditionalAccessPolicyConfiguration, bool> includes) =>
            others.Any(o => !o.HasExclusions && (o.TargetsAllUsers || includes(o.Policy)));
        string Named(IReadOnlyList<string> ids) => label is null
            ? ""
            : ": " + string.Join(", ", ids.Take(10).Select(label)) + (ids.Count > 10 ? "…" : "");

        var open = new List<string>();
        var users = p.ExcludeUsers.Where(u => !u.Equals(GuestsValue, StringComparison.OrdinalIgnoreCase))
            .Where(u => !scope.Covers(others, u)).ToList();
        if (users.Count > 0)
            open.Add(Plural(users.Count, "usuário excluído nominalmente", "usuários excluídos nominalmente") + Named(users));

        var unread = new List<string>();
        foreach (var g in p.ExcludeGroups)
        {
            if (Structural(o => o.IncludeGroups.Contains(g, Ci))) continue;
            if (!scope.Membership.IsComplete(g)) { unread.Add(g); continue; }
            var left = scope.Membership.Get(g)!.UserIds.Distinct(Ci).Where(u => !scope.Covers(others, u)).ToList();
            if (left.Count > 0)
                open.Add(Plural(left.Count, "conta excluída", "contas excluídas") + " via grupo " + scope.Membership.Describe(g) + Named(left));
        }
        if (unread.Count > 0)
            open.Add(scope.Membership.Collected
                ? Plural(unread.Count, "grupo excluído", "grupos excluídos") + " com pertencimento não lido por completo: "
                  + string.Join(", ", unread.Select(scope.Membership.Describe))
                : Plural(unread.Count, "grupo excluído", "grupos excluídos") + " (pertencimento não coletado)");

        var excludedRoles = p.ExcludeRoles.Where(role => !Structural(o => o.IncludeRoles.Contains(role, Ci))).ToList();
        if (excludedRoles.Count > 0) open.Add(Plural(excludedRoles.Count, "papel excluído", "papéis excluídos"));
        if (ExcludesGuests(p) && !Structural(IncludesGuests)) open.Add("convidados e usuários externos");
        return open;
    }

    private static IEnumerable<string> CoveringLegacyPolicies(
        ConditionalAccessPolicyReading policy, IReadOnlyList<ConditionalAccessPolicyReading> fullBlocks, CaScope scope)
    {
        var p = policy.Policy;
        foreach (var o in fullBlocks.Where(o => !ReferenceEquals(o, policy)))
        {
            var only = new[] { o };
            var coversUser = p.ExcludeUsers.Any(u => scope.Covers(only, u))
                || p.ExcludeGroups.Any(g => scope.Membership.IsComplete(g) && scope.Membership.Get(g)!.UserIds.Any(u => scope.Covers(only, u)));
            var coversRest = !o.HasExclusions && (
                p.ExcludeGroups.Any(g => o.TargetsAllUsers || o.Policy.IncludeGroups.Contains(g, Ci))
                || p.ExcludeRoles.Any(role => o.TargetsAllUsers || o.Policy.IncludeRoles.Contains(role, Ci))
                || (ExcludesGuests(p) && (o.TargetsAllUsers || IncludesGuests(o.Policy))));
            if (coversUser || coversRest) yield return o.Policy.Id;
        }
    }

    // ---- Alcance conta a conta -------------------------------------------------------------------------

    /// <summary>
    /// [AEGIS-KNIGHT-CA-GROUP-SCOPE-01] As políticas que impõem a exigência (<paramref name="policies"/>, já filtradas por estado,
    /// aplicação, condição e controle — nada disso é relaxado aqui) somadas e conferidas CONTA A CONTA sobre a enumeração completa
    /// das contas habilitadas da mesma coleta. Uma conta é alcançada se alguma a alcança; desconhecida se nenhuma a alcança e
    /// alguma depende de leitura incompleta; excluída se só exclusões explícitas a deixam de fora; fora do alvo nos demais casos.
    /// Quantidades iguais nunca aprovam — a comparação é pela identidade — e a ausência de contas nunca aprova.
    /// </summary>
    internal static ScopedCoverage CoverageByAccount(IReadOnlyList<ConditionalAccessPolicyReading> policies, CaScope scope)
    {
        var none = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!scope.Membership.Collected)
            return new(ScopedVerdict.Unverifiable, "o pertencimento dos grupos não foi coletado nesta aquisição",
                Array.Empty<string>(), Array.Empty<string>(), none);
        if (scope.PopulationGap(policies) is { } gap)
            return new(ScopedVerdict.Unverifiable, gap, Array.Empty<string>(), Array.Empty<string>(), none);

        var population = scope.Population!;
        var reachedBy = policies.ToDictionary(r => r.Policy.Id, _ => 0, StringComparer.Ordinal);
        var reached = 0;
        var excepted = new List<string>();
        var outside = 0;
        var unknown = 0;
        foreach (var account in population)
        {
            var applies = false;
            var maybe = false;
            var excluded = false;
            foreach (var r in policies)
            {
                switch (scope.Of(r, account))
                {
                    case Reach.Applies: applies = true; reachedBy[r.Policy.Id]++; break;
                    case Reach.Unknown: maybe = true; break;
                    case Reach.Excluded: excluded = true; break;
                }
            }
            if (applies) reached++;
            else if (maybe) unknown++;
            else if (excluded) excepted.Add(account);
            else outside++;
        }

        var total = population.Count;
        var reach = policies.Where(r => !r.TargetsAllUsers).ToDictionary(r => r.Policy.Id,
            r => $"alvo em {scope.Target(r.Policy)}: {reachedBy[r.Policy.Id]} de {total} conta(s) habilitada(s) alcançada(s), conferidas conta a conta",
            StringComparer.Ordinal);
        var covering = policies.Where(r => reachedBy[r.Policy.Id] > 0).Select(r => r.Policy.Id).ToList();

        if (outside > 0)
            return new(ScopedVerdict.Gap,
                $"{outside} de {total} conta(s) habilitada(s) fora do alcance de toda política habilitada que a impõe, conferidas conta a conta pelo pertencimento lido",
                Array.Empty<string>(), excepted, reach);
        if (unknown > 0)
            return new(ScopedVerdict.Unknown,
                $"alcance não comprovado para {unknown} de {total} conta(s) habilitada(s) — falta {scope.MissingReadings(policies)}",
                Array.Empty<string>(), excepted, reach);
        if (reached == 0)
            return new(ScopedVerdict.NoneReached,
                $"nenhuma das {total} conta(s) habilitada(s) é alcançada ({excepted.Count} excluída(s) explicitamente) — as políticas não protegem conta alguma",
                Array.Empty<string>(), excepted, reach);
        if (excepted.Count > 0)
            return new(ScopedVerdict.Exceptions,
                $"{reached} de {total} conta(s) habilitada(s) alcançada(s) e {excepted.Count} excluída(s) explicitamente, sem outra política que as alcance",
                Array.Empty<string>(), excepted, reach);
        return new(ScopedVerdict.Covered,
            $"{reached} de {total} conta(s) habilitada(s) alcançada(s), conferidas conta a conta pelo pertencimento lido",
            covering, excepted, reach);
    }

    // ---- Base mínima -------------------------------------------------------------------------------

    /// <summary>
    /// A base mínima é uma exigência AMPLA: habilitada, exigindo MFA, com alvo DECLARADO em todos os usuários, em
    /// todas as aplicações e sem condição que a estreite. Exclusões nominais, de papel e de convidados são
    /// delimitadas e ficam listadas na evidência; exclusão de GRUPO não — o grupo pode conter quase todos, e seu
    /// pertencimento não é coletado. Política para um usuário, um papel ou uma aplicação não sustenta uma
    /// conclusão sobre o ambiente.
    /// </summary>
    /// <remarks>
    /// [AEGIS-KNIGHT-COLLECTION-GAPS-01] Com o pertencimento lido, o alcance é conferido CONTA A CONTA, pelo critério do controle
    /// (política habilitada, exigindo MFA, em todas as aplicações e sem condição que a estreite — nada disso é relaxado):
    ///   • todos os usuários, com exclusão de grupo lido por completo — a exclusão fica delimitada (as contas excluídas são
    ///     contadas e listadas na evidência), como as nominais: base mínima;
    ///   • dirigida a grupo(s) — cada conta habilitada da enumeração COMPLETA da mesma coleta é conferida pela identidade:
    ///     alcançada, excluída explicitamente, fora do alvo ou desconhecida (grupo não lido por completo, condição de
    ///     convidado, papel detido via grupo). Uma conta fora do alvo prova que não é base para o ambiente; uma desconhecida
    ///     deixa não resolvida; só todas alcançadas (ou excluídas explicitamente) sustentam a base. Quantidades iguais nunca
    ///     aprovam — a comparação é por id;
    ///   • sem a enumeração completa (teto, falha, aquisição anterior sem os ids) — não resolvida, com o motivo.
    /// Cobrir os administradores não demonstra base mínima para o ambiente: o alcance é sempre o de todas as contas.
    /// Revisão (catálogo v11): base exige ao menos uma conta efetivamente alcançada — política cujas exclusões alcançam cada
    /// conta habilitada da enumeração não protege ninguém (alcance restrito, mesmo com alvo declarado em todos), e enumeração
    /// sem conta habilitada deixa a política dirigida a grupo não resolvida — a ausência de contas nunca aprova.
    /// </remarks>
    private static MfaBaselineConclusion ConcludeBaseline(
        IReadOnlyList<ConditionalAccessPolicyReading> readings, GroupMembershipIndex membership,
        IReadOnlyList<DirectoryRoleConfiguration>? roles, IReadOnlyList<string>? enabledAccounts)
    {
        var baseline = new List<string>();
        var unresolved = new List<string>();
        var restricted = new List<string>();
        var reach = new Dictionary<string, string>(StringComparer.Ordinal);
        var (rolesOf, groupRolesOf) = RoleHoldings(roles ?? Array.Empty<DirectoryRoleConfiguration>(), membership);
        var population = enabledAccounts?.Distinct(Ci).ToList();
        foreach (var r in readings.Where(r => r.IsEnforced && r.RequiresMfa))
        {
            var p = r.Policy;
            var broadScope = r.CoversAllApplications && r.Narrowing.Count == 0;
            if (!broadScope) { restricted.Add(p.Id); continue; }

            if (r.TargetsAllUsers)
            {
                // Alvo declarado em todos, mas exclusões que alcançam cada conta habilitada da enumeração: ninguém sujeito ao MFA.
                if (r.HasExclusions && population is { Count: > 0 }
                    && population.All(a => ReachOf(r, a, rolesOf.TryGetValue(a, out var held) ? held : Array.Empty<string>(),
                        Array.Empty<string>(), membership) == Reach.Excluded))
                {
                    restricted.Add(p.Id);
                    reach[p.Id] = "alvo declarado em todos os usuários: " + NoneReached(population.Count, population.Count);
                    continue;
                }
                if (p.ExcludeGroups.Count == 0) { baseline.Add(p.Id); continue; }
                var open = p.ExcludeGroups.Where(g => !membership.IsComplete(g)).ToList();
                if (open.Count == 0)
                {
                    baseline.Add(p.Id);
                    var excluded = p.ExcludeGroups.SelectMany(g => membership.Get(g)!.UserIds).Distinct(Ci).Count();
                    reach[p.Id] = $"exclusão por grupo delimitada pelo pertencimento lido: {Plural(excluded, "conta excluída", "contas excluídas")} via "
                        + string.Join(", ", p.ExcludeGroups.Select(membership.Describe));
                }
                else
                {
                    unresolved.Add(p.Id);
                    if (membership.Collected)
                        reach[p.Id] = "exclui grupo com pertencimento não lido por completo: " + string.Join(", ", open.Select(membership.Describe));
                }
                continue;
            }

            if (p.IncludeGroups.Count == 0) { restricted.Add(p.Id); continue; }

            // Sem o pertencimento coletado (aquisição anterior), a leitura de antes: não resolvida, texto de então.
            if (!membership.Collected) { unresolved.Add(p.Id); continue; }

            var target = string.Join(", ", p.IncludeGroups.Select(membership.Describe));
            var openExcluded = p.ExcludeGroups.Where(g => !membership.IsComplete(g)).ToList();
            if (openExcluded.Count > 0)
                target += "; exclui grupo com pertencimento não lido por completo: " + string.Join(", ", openExcluded.Select(membership.Describe));
            if (population is null)
            {
                unresolved.Add(p.Id);
                reach[p.Id] = $"dirigida a {target}, sem a enumeração completa das contas habilitadas do diretório nesta coleta para conferir conta a conta";
                continue;
            }
            if (population.Count == 0)
            {
                // Sem contas não há alcance a demonstrar — a ausência de contas nunca aprova.
                unresolved.Add(p.Id);
                reach[p.Id] = $"dirigida a {target}, mas a enumeração desta coleta não retornou nenhuma conta habilitada — não há alcance a demonstrar";
                continue;
            }
            if (roles is null && (p.IncludeRoles.Count > 0 || p.ExcludeRoles.Count > 0))
            {
                unresolved.Add(p.Id);
                reach[p.Id] = $"dirigida a {target} e a papéis, sem o inventário de papéis nesta coleta para conferir conta a conta";
                continue;
            }

            var applies = 0;
            var excludedAccounts = 0;
            var outside = 0;
            var unknown = 0;
            foreach (var account in population)
            {
                var direct = rolesOf.TryGetValue(account, out var d) ? d : Array.Empty<string>();
                var viaGroup = groupRolesOf.TryGetValue(account, out var v) ? v.Where(t => !direct.Contains(t, Ci)).ToList() : new List<string>();
                switch (ReachOf(r, account, direct, viaGroup, membership))
                {
                    case Reach.Applies: applies++; break;
                    case Reach.Excluded: excludedAccounts++; break;
                    case Reach.NotTargeted: outside++; break;
                    default: unknown++; break;
                }
            }

            if (outside > 0)
            {
                restricted.Add(p.Id);
                reach[p.Id] = $"dirigida a {target}: {outside} de {population.Count} conta(s) habilitada(s) fora do alvo, conferidas conta a conta — "
                    + "não é base mínima para o ambiente";
            }
            else if (unknown > 0)
            {
                unresolved.Add(p.Id);
                reach[p.Id] = $"dirigida a {target}: alcance não comprovado para {unknown} de {population.Count} conta(s) habilitada(s) (grupo não lido "
                    + "por completo, condição de convidado ou papel detido via grupo)";
            }
            else if (applies == 0)
            {
                // Todas excluídas explicitamente: nenhuma conta sujeita ao MFA desta política — não protege ninguém.
                restricted.Add(p.Id);
                reach[p.Id] = $"dirigida a {target}: {NoneReached(population.Count, excludedAccounts)}";
            }
            else
            {
                baseline.Add(p.Id);
                reach[p.Id] = $"alvo em {target}, com alcance comprovado conta a conta: {applies} de {population.Count} conta(s) habilitada(s) alcançada(s)"
                    + (excludedAccounts > 0 ? $" e {excludedAccounts} excluída(s) explicitamente" : "");
            }
        }

        string? reason = null;
        if (baseline.Count == 0 && unresolved.Count > 0)
        {
            string NameOf(string id) => "“" + Name(readings.First(x => x.Policy.Id == id).Policy) + "”";
            reason = membership.Collected
                ? "Há política habilitada que exige MFA em todas as aplicações cujo alcance sobre todas as contas não pôde ser comprovado ("
                  + string.Join("; ", unresolved.Select(id => NameOf(id) + (reach.TryGetValue(id, out var why) ? ": " + why : "")))
                  + ") — não é possível afirmar nem negar uma base mínima para o ambiente."
                : "Há política habilitada que exige MFA em todas as aplicações, mas o alcance dela depende de grupo(s) "
                  + "cujo pertencimento não é coletado nesta entrega ("
                  + string.Join(", ", unresolved.Select(NameOf))
                  + ") — não é possível afirmar nem negar uma base mínima para o ambiente.";
        }
        return new MfaBaselineConclusion(baseline, unresolved, restricted, reason, reach);
    }

    private static string NoneReached(int population, int excluded) =>
        $"nenhuma das {population} conta(s) habilitada(s) é alcançada ({excluded} excluída(s) explicitamente) — a política não protege "
        + "conta alguma e não é base mínima para o ambiente";

    // ---- Tradução em sinais do KNIGHT -----------------------------------------------------------------

    /// <summary>
    /// Os sinais que as regras do catálogo consomem. Políticas não coletadas → sinais AUSENTES (com o motivo
    /// da capacidade), nunca "nenhuma política". Conclusão que não pode ser afirmada nem negada (pertencimento
    /// não coletado, exceções explícitas sem outra cobertura) → sinal ausente com o motivo — nunca zero.
    /// </summary>
    public static IReadOnlyList<KnightObservation> ToObservations(ConditionalAccessAnalysis? analysis, string? missingReason)
    {
        if (analysis is null)
        {
            var reason = missingReason ?? "Políticas de acesso condicional não coletadas.";
            return new[]
            {
                KnightObservation.MissingData(KnightSignalKey.LegacyAuthenticationBlocked, reason),
                KnightObservation.MissingData(KnightSignalKey.PrivilegedRolesWithoutMfaPolicy, reason),
                KnightObservation.MissingData(KnightSignalKey.BaselineMfaPolicies, reason),
            };
        }

        var legacy = analysis.LegacyAuth;
        var baseline = analysis.Baseline;
        return new[]
        {
            legacy.Blocked || legacy.InconclusiveReason is null
                ? KnightObservation.OfFlag(KnightSignalKey.LegacyAuthenticationBlocked, legacy.Blocked)
                : KnightObservation.MissingData(KnightSignalKey.LegacyAuthenticationBlocked, legacy.InconclusiveReason),
            analysis.AdminMfa.Evaluable
                ? KnightObservation.OfCount(KnightSignalKey.PrivilegedRolesWithoutMfaPolicy, analysis.AdminMfa.UncoveredRoleCount)
                : KnightObservation.MissingData(KnightSignalKey.PrivilegedRolesWithoutMfaPolicy,
                    analysis.AdminMfa.InconclusiveReason ?? "Cobertura de MFA administrativa não resolvida."),
            baseline.BaselinePolicyIds.Count > 0 || baseline.InconclusiveReason is null
                ? KnightObservation.OfCount(KnightSignalKey.BaselineMfaPolicies, baseline.BaselinePolicyIds.Count)
                : KnightObservation.MissingData(KnightSignalKey.BaselineMfaPolicies, baseline.InconclusiveReason),
        };
    }

    // ---- Descrição legível -----------------------------------------------------------------------------

    public static string StateLabel(ConditionalAccessPolicyState state) => state switch
    {
        ConditionalAccessPolicyState.Enabled => "habilitada",
        ConditionalAccessPolicyState.Disabled => "desabilitada",
        ConditionalAccessPolicyState.ReportOnly => "somente relatório (não impõe)",
        _ => "estado não reconhecido",
    };

    public static string Name(ConditionalAccessPolicyConfiguration p) =>
        string.IsNullOrWhiteSpace(p.DisplayName) ? p.Id : p.DisplayName!;

    private static string Describe(
        ConditionalAccessPolicyConfiguration p, bool requiresMfa, bool alternative, bool blocks, IReadOnlyList<string> narrowing)
    {
        var parts = new List<string> { "Estado: " + StateLabel(p.State) };

        var users = new List<string>();
        if (p.IncludeUsers.Contains("All", Ci)) users.Add("todos os usuários");
        else if (p.IncludeUsers.Contains("None", Ci) && p.IncludeRoles.Count == 0 && p.IncludeGroups.Count == 0) users.Add("nenhum usuário");
        var nominal = p.IncludeUsers.Count(u => !IsSpecialUser(u));
        if (nominal > 0) users.Add($"{nominal} usuário(s) nominal(is)");
        if (p.IncludeRoles.Count > 0) users.Add($"{p.IncludeRoles.Count} papel(éis)");
        if (p.IncludeGroups.Count > 0) users.Add($"{p.IncludeGroups.Count} grupo(s)");
        if (IncludesGuests(p)) users.Add("convidados/externos");
        var excl = new List<string>();
        var nominalExcluded = p.ExcludeUsers.Count(u => !IsSpecialUser(u));
        if (nominalExcluded > 0) excl.Add($"{nominalExcluded} usuário(s)");
        if (p.ExcludeGroups.Count > 0) excl.Add($"{p.ExcludeGroups.Count} grupo(s)");
        if (p.ExcludeRoles.Count > 0) excl.Add($"{p.ExcludeRoles.Count} papel(éis)");
        if (ExcludesGuests(p)) excl.Add("convidados/externos");
        parts.Add("Alvos: " + (users.Count == 0 ? "não identificados" : string.Join(", ", users))
            + (excl.Count > 0 ? " (exceto " + string.Join(", ", excl) + ")" : ""));

        string apps;
        if (p.IncludeApplications.Contains("All", Ci))
            apps = p.ExcludeApplications.Count == 0 ? "todas" : $"todas, exceto {p.ExcludeApplications.Count}";
        else if (p.IncludeApplications.Contains("MicrosoftAdminPortals", Ci))
            apps = "portais administrativos da Microsoft";
        else if (p.IncludeApplications.Count > 0)
            apps = $"{p.IncludeApplications.Count} aplicação(ões) específica(s)";
        else if (p.IncludeUserActions.Count > 0)
            apps = "ações do usuário (" + string.Join(", ", p.IncludeUserActions) + ")";
        else if (p.IncludeAuthenticationContexts.Count > 0)
            apps = "contextos de autenticação";
        else
            apps = "não identificadas";
        parts.Add("Aplicações: " + apps);

        if (!AllClientTypes(p.ClientAppTypes))
            parts.Add("Clientes: " + string.Join(", ", p.ClientAppTypes.Select(ClientLabel)));
        parts.Add("Condições: " + (narrowing.Count(n => n != RestrictedClients) == 0
            ? "nenhuma que estreite"
            : string.Join(", ", narrowing.Where(n => n != RestrictedClients))));

        string grant;
        if (blocks) grant = "bloquear acesso";
        else if (requiresMfa)
            grant = string.IsNullOrWhiteSpace(p.AuthenticationStrengthId)
                ? "exigir MFA"
                : $"exigir força de autenticação “{p.AuthenticationStrengthName ?? p.AuthenticationStrengthId}”";
        else if (alternative) grant = "MFA como alternativa (operador OU)";
        else if (p.BuiltInControls.Count > 0) grant = string.Join(" / ", p.BuiltInControls);
        else grant = "sem controle de concessão";
        parts.Add("Concessão: " + grant);

        return string.Join(" · ", parts);
    }

    private static bool IsSpecialUser(string u) =>
        u.Equals("All", StringComparison.OrdinalIgnoreCase) || u.Equals("None", StringComparison.OrdinalIgnoreCase)
        || u.Equals(GuestsValue, StringComparison.OrdinalIgnoreCase);

    private static string ClientLabel(string c) => c.ToLowerInvariant() switch
    {
        "browser" => "navegador",
        "mobileappsanddesktopclients" => "aplicativos móveis e de desktop",
        "exchangeactivesync" => "Exchange ActiveSync",
        "other" => "outros clientes (legados)",
        "eassupported" => "Exchange ActiveSync (suportado)",
        _ => c,
    };

    /// <summary>Contagem legível ("1 papel", "3 papéis") — só para textos de evidência.</summary>
    public static string Plural(int n, string singular, string plural) =>
        n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? singular : plural);
}
