using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AegisScore.Domain;

namespace AegisScore.Application.Knight.Configuration;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-02] ALCANCE de um critério que vive numa política do Teams
// ============================================================================
// No Teams quase todo critério de configuração vive numa política que existe em VÁRIAS instâncias ao mesmo tempo:
// a PADRÃO DA ORGANIZAÇÃO ("Global"), que vale para quem não tem outra, e as PERSONALIZADAS, que valem para quem
// as tiver atribuídas. Por isso duas afirmações que parecem a mesma coisa não são:
//
//   • "a política padrão da organização atende ao critério"  →  é um fato sobre UMA instância;
//   • "o critério vale no ambiente"                          →  exige que NENHUMA instância atribuível o viole.
//
// Esta classe só afirma a segunda quando pode. A regra é única para os três tipos de política:
//
//   1. sem a política padrão na coleta            → NÃO AVALIADO (falta a instância que sempre se aplica);
//   2. política padrão ilegível para o critério   → NÃO AVALIADO com o motivo;
//   3. qualquer instância viola o critério        → EXPOSTO, e os AFETADOS são as políticas que violam;
//   4. alguma personalizada ilegível, sem violação demonstrada → NÃO AVALIADO (aprovar exigiria supor);
//   5. todas atendem                              → APROVADO, com cada instância como evidência.
//
// O ALCANCE de cada política é dito com o que a coleta tem: a padrão alcança quem não tem outra; a personalizada
// alcança os GRUPOS a que está atribuída (Get-CsGroupPolicyAssignment). A atribuição DIRETA por usuário NÃO é
// enumerada nesta coleta — e isso é declarado como limitação, nunca preenchido com usuários inventados.

/// <summary>Como UMA instância de política se comporta diante do critério.</summary>
public enum TeamsPolicyState
{
    /// <summary>A instância atende ao critério.</summary>
    Compliant = 0,

    /// <summary>A instância viola o critério — é um fato demonstrável, não uma suspeita.</summary>
    NonCompliant = 1,

    /// <summary>
    /// A fonte não informou o valor, ou devolveu um valor que o contrato não reconhece. Nunca vira aprovação
    /// nem reprovação: vira motivo.
    /// </summary>
    Indeterminate = 2,
}

/// <summary>Leitura de UMA instância de política para UM critério: o estado e o valor observado, em palavras.</summary>
/// <param name="Observed">O que a fonte devolveu, já legível ("Sim", "Everyone", "EnabledExceptAnonymous"…).</param>
public sealed record TeamsPolicyReading(TeamsPolicyState State, string Observed)
{
    public static TeamsPolicyReading Compliant(string observed) => new(TeamsPolicyState.Compliant, observed);
    public static TeamsPolicyReading NonCompliant(string observed) => new(TeamsPolicyState.NonCompliant, observed);

    /// <summary>Valor ausente na fonte — o critério não pode ser afirmado nem negado nesta instância.</summary>
    public static TeamsPolicyReading Unknown() => new(TeamsPolicyState.Indeterminate, "não informado pela fonte");

    /// <summary>Valor presente, mas fora dos valores documentados do contrato: preservado como veio.</summary>
    public static TeamsPolicyReading Unrecognized(string value) =>
        new(TeamsPolicyState.Indeterminate, "valor não reconhecido (" + value + ")");

    /// <summary>Atalho para critérios booleanos: o valor esperado decide o estado.</summary>
    public static TeamsPolicyReading Flag(bool? value, bool expected, string whenTrue = "Sim", string whenFalse = "Não") =>
        value is null
            ? Unknown()
            : new(value.Value == expected ? TeamsPolicyState.Compliant : TeamsPolicyState.NonCompliant,
                value.Value ? whenTrue : whenFalse);

    /// <summary>Atalho para critérios de valor nominal: o conjunto aceito decide o estado.</summary>
    public static TeamsPolicyReading OneOf(string? value, IReadOnlyCollection<string> accepted, IReadOnlyCollection<string>? known = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return Unknown();
        var v = value.Trim();
        if (accepted.Contains(v, StringComparer.OrdinalIgnoreCase)) return Compliant(v);
        if (known is null || known.Contains(v, StringComparer.OrdinalIgnoreCase)) return NonCompliant(v);
        return Unrecognized(v);
    }
}

public static class TeamsPolicyReach
{
    /// <summary>Limite de grupos nomeados no texto de alcance de UMA política (a contagem é sempre dita).</summary>
    private const int MaxGroupsListed = 10;

    private const string DirectAssignmentLimitation =
        "As atribuições DIRETAS de política por conta de usuário não são enumeradas nesta coleta: o alcance "
        + "demonstrado é o da política padrão da organização e o das atribuições a grupos. Pessoas com a política "
        + "atribuída diretamente não foram contadas nem estimadas.";

    /// <summary>
    /// Avalia um critério em TODAS as instâncias de um tipo de política e devolve o veredito com os objetos que o
    /// sustentam. <paramref name="read"/> é a única parte específica do controle.
    /// </summary>
    /// <param name="settingId">Identificador estável da configuração lida (ex.: "TeamsMeetingPolicy/AutoAdmittedUsers").</param>
    /// <param name="settingLabel">Nome legível da configuração, o mesmo em tela, HTML, CSV e PDF.</param>
    /// <param name="expected">O que se espera encontrar, em palavras.</param>
    public static KnightControlOutcome Evaluate<TPolicy>(
        KnightEvaluationContext context,
        string settingId,
        string settingLabel,
        string expected,
        Func<TPolicy, TeamsPolicyReading> read,
        string exposedEvidence,
        string passedEvidence)
        where TPolicy : class, ITeamsPolicyDocument
    {
        ArgumentNullException.ThrowIfNull(context);

        var policies = context.Configuration.Read<TPolicy>();
        if (!policies.Collected)
            return KnightControlOutcome.NotEvaluated(policies.MissingReason ?? "as políticas do Teams não foram coletadas nesta aquisição.");
        if (policies.Items.Count == 0)
            return KnightControlOutcome.NotEvaluated(
                "a coleta concluiu sem devolver nenhuma política deste tipo — nem a padrão da organização; sem ela não há o que avaliar.");

        var reach = PolicyReach.Build(context, policies.Items[0].PolicyType);

        var readings = policies.Items
            .Select(p => (Policy: (ITeamsPolicyDocument)p, Reading: read(p)))
            .OrderBy(x => TeamsPolicyIdentities.IsGlobal(x.Policy.Identity) ? 0 : 1)
            .ThenBy(x => TeamsPolicyIdentities.Name(x.Policy.Identity), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var global = readings.FirstOrDefault(x => TeamsPolicyIdentities.IsGlobal(x.Policy.Identity));
        if (global.Policy is null)
            return KnightControlOutcome.NotEvaluated(
                "a coleta não devolveu a política padrão da organização; sem ela não é possível afirmar o que vale para quem não tem política personalizada.");

        if (global.Reading.State == TeamsPolicyState.Indeterminate)
            return KnightControlOutcome.NotEvaluated(
                $"a política padrão da organização não informou “{settingLabel}” ({global.Reading.Observed}).",
                readings.Select(x => Evidence(x, settingId, settingLabel, expected, reach)).ToList());

        var failing = readings.Where(x => x.Reading.State == TeamsPolicyState.NonCompliant).ToList();
        var unreadable = readings.Where(x => x.Reading.State == TeamsPolicyState.Indeterminate).ToList();

        var evidence = readings
            .Where(x => x.Reading.State != TeamsPolicyState.NonCompliant)
            .Select(x => Evidence(x, settingId, settingLabel, expected, reach))
            .ToList();

        if (failing.Count == 0)
        {
            if (unreadable.Count > 0)
                return KnightControlOutcome.NotEvaluated(
                    Unreadable(unreadable, settingLabel) + " A política padrão da organização atende ao critério, mas aprovar o ambiente exigiria supor o valor dessas instâncias.",
                    evidence);

            return KnightControlOutcome.Passed(
                passedEvidence + " " + Scope(readings.Count) + " " + DirectAssignmentLimitation, evidence);
        }

        var affected = failing.Select(x => Affected(x, settingId, settingLabel, expected, reach)).ToList();
        var limitation = DirectAssignmentLimitation
            + (unreadable.Count > 0 ? " " + Unreadable(unreadable, settingLabel) : "")
            + (reach.AssignmentsCollected ? "" : " " + reach.AssignmentsMissingReason);

        return KnightControlOutcome.Exposed(
            exposedEvidence + " " + Failing(failing),
            affected, evidence, complete: unreadable.Count == 0, limitation: limitation);
    }

    // ---- Alcance --------------------------------------------------------------------------------------

    /// <summary>Atribuições a grupos do MESMO tipo de política, indexadas pelo nome da política.</summary>
    private sealed class PolicyReach
    {
        private readonly ILookup<string, TeamsPolicyAssignment> _byPolicy;

        private PolicyReach(ILookup<string, TeamsPolicyAssignment> byPolicy, bool collected, string? missingReason)
        {
            _byPolicy = byPolicy;
            AssignmentsCollected = collected;
            AssignmentsMissingReason = missingReason
                ?? "As atribuições a grupos não foram coletadas nesta aquisição; o alcance das políticas personalizadas não pôde ser demonstrado.";
        }

        public bool AssignmentsCollected { get; }
        public string AssignmentsMissingReason { get; }

        public IReadOnlyList<TeamsPolicyAssignment> For(string? identity) =>
            AssignmentsCollected ? _byPolicy[TeamsPolicyIdentities.Name(identity)].ToList() : Array.Empty<TeamsPolicyAssignment>();

        public static PolicyReach Build(KnightEvaluationContext context, string? policyType)
        {
            var read = context.Configuration.Read<TeamsPolicyAssignment>();
            if (!read.Collected || policyType is null)
                return new PolicyReach(
                    Array.Empty<TeamsPolicyAssignment>().ToLookup(a => "", StringComparer.OrdinalIgnoreCase),
                    false, read.MissingReason);

            var lookup = read.Items
                .Where(a => string.Equals(a.PolicyType, policyType, StringComparison.OrdinalIgnoreCase))
                .ToLookup(a => a.PolicyName ?? "", StringComparer.OrdinalIgnoreCase);
            return new PolicyReach(lookup, true, null);
        }
    }

    /// <summary>Alcance de UMA política, em palavras, com o que a coleta tem — nunca mais do que isso.</summary>
    private static string ReachText(string? identity, PolicyReach reach)
    {
        if (TeamsPolicyIdentities.IsGlobal(identity))
            return "Alcance: vale para todas as contas às quais nenhuma política personalizada esteja atribuída.";

        if (!reach.AssignmentsCollected)
            return "Alcance não demonstrado: " + reach.AssignmentsMissingReason;

        var groups = reach.For(identity);
        if (groups.Count == 0)
            return "Alcance não demonstrado: nenhuma atribuição a grupo foi encontrada nesta coleta. "
                + "Se a política estiver atribuída diretamente a contas de usuário, esse alcance não foi enumerado.";

        var ids = groups.Select(g => g.GroupId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var listed = string.Join(", ", ids.Take(MaxGroupsListed));
        var rank = groups.Where(g => g.Rank.HasValue).Select(g => g.Rank!.Value).DefaultIfEmpty().Min();
        return $"Alcance: atribuída a {N(ids.Count)} grupo(s) — {listed}{(ids.Count > MaxGroupsListed ? "…" : "")}"
            + (rank > 0 ? $" (precedência {rank.ToString(CultureInfo.InvariantCulture)} entre as atribuições de grupo)" : "")
            + ". Os membros de cada grupo não são enumerados nesta coleta.";
    }

    private static KnightIndicatorObject Evidence(
        (ITeamsPolicyDocument Policy, TeamsPolicyReading Reading) x,
        string settingId, string settingLabel, string expected, PolicyReach reach) =>
        KnightObjects.Evidence(KnightAffectedObjectKind.Policy, PolicyId(settingId, x.Policy.Identity),
            PolicyDisplay(x.Policy.Identity),
            $"Encontrado: {x.Reading.Observed}. Esperado: {expected}. " + ReachText(x.Policy.Identity, reach),
            $"{settingLabel}: {x.Reading.Observed}");

    private static KnightIndicatorObject Affected(
        (ITeamsPolicyDocument Policy, TeamsPolicyReading Reading) x,
        string settingId, string settingLabel, string expected, PolicyReach reach) =>
        KnightObjects.Affected(KnightAffectedObjectKind.Policy, PolicyId(settingId, x.Policy.Identity),
            PolicyDisplay(x.Policy.Identity), null,
            $"Encontrado: {x.Reading.Observed}. Esperado: {expected}. " + ReachText(x.Policy.Identity, reach),
            observed: $"{settingLabel}: {x.Reading.Observed}");

    /// <summary>
    /// Identificador do objeto: a configuração lida MAIS a política onde ela foi lida. Duas políticas com o mesmo
    /// problema são dois objetos distintos, e a mesma política em dois controles não se confunde.
    /// </summary>
    private static string PolicyId(string settingId, string? identity) =>
        settingId + "@" + (TeamsPolicyIdentities.IsGlobal(identity) ? TeamsPolicyIdentities.Global : TeamsPolicyIdentities.Name(identity));

    private static string PolicyDisplay(string? identity) =>
        TeamsPolicyIdentities.IsGlobal(identity)
            ? "Política padrão da organização"
            : "Política “" + TeamsPolicyIdentities.Name(identity) + "”";

    private static string Failing(IReadOnlyList<(ITeamsPolicyDocument Policy, TeamsPolicyReading Reading)> failing)
    {
        var names = failing.Select(f => TeamsPolicyIdentities.Label(f.Policy.Identity)).ToList();
        var joined = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " e " + names[^1];
        var head = failing.Count == 1 ? "Encontrado em 1 política: " : $"Encontrado em {N(failing.Count)} políticas: ";
        var global = failing.Any(f => TeamsPolicyIdentities.IsGlobal(f.Policy.Identity));
        return head + joined + ". "
            + (global
                ? "Como a política padrão da organização está entre elas, a condição vale para todas as contas sem política personalizada atribuída."
                : "A política padrão da organização atende ao critério; a condição vale para quem tiver as políticas acima atribuídas.");
    }

    private static string Scope(int total) =>
        total == 1
            ? "Só existe a política padrão da organização nesta coleta."
            : $"Verificado em {N(total)} políticas deste tipo (a padrão da organização e as personalizadas).";

    private static string Unreadable(
        IReadOnlyList<(ITeamsPolicyDocument Policy, TeamsPolicyReading Reading)> unreadable, string settingLabel) =>
        $"{N(unreadable.Count)} política(s) não informaram “{settingLabel}”: "
        + string.Join(", ", unreadable.Select(u => TeamsPolicyIdentities.Label(u.Policy.Identity) + " (" + u.Reading.Observed + ")"))
        + ".";

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
}
