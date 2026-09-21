using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AegisScore.Domain;

namespace AegisScore.Application.Knight.Configuration;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-03] ALCANCE de um critério que vive numa política do Exchange Online
// ============================================================================
// Vale aqui a mesma distinção firmada no bloco do Teams — "a política padrão atende" ≠ "o critério vale no
// ambiente" —, mas o desenho da fonte é outro, e a diferença muda o que pode ser afirmado:
//
//   • no Teams a política padrão tem uma IDENTIDADE literal, e o alcance das personalizadas só podia ser
//     demonstrado por atribuição a grupos (a atribuição direta por conta ficava declarada como limitação);
//   • no Exchange, cada CAIXA DE CORREIO declara qual política de cada tipo a governa. Essa declaração é lida
//     na MESMA enumeração que a coleta já faz — sem nenhuma consulta nova — e vira uma contagem por política.
//     O alcance, portanto, é demonstrável: "esta política governa N caixas de correio".
//
// O que continua NÃO sendo demonstrável, e é dito como limitação: quando a enumeração de caixas foi truncada ou
// não foi concluída, a contagem não cobre a organização inteira. E uma política sem nenhuma caixa contada não é
// "política sem uso": é política sem alcance demonstrado nesta coleta.
//
// A regra de veredito é única para todos os tipos:
//   1. tipo não coletado                                 → NÃO AVALIADO com o motivo da coleta;
//   2. nenhuma instância na coleta                       → NÃO AVALIADO (não há o que avaliar);
//   3. qualquer instância VIOLA o critério               → EXPOSTO, afetadas são as que violam;
//   4. alguma instância ilegível, sem violação           → NÃO AVALIADO (aprovar exigiria supor);
//   5. nenhuma instância declarada PADRÃO, sem violação  → NÃO AVALIADO (falta a que vale por omissão);
//   6. todas atendem                                     → APROVADO, com cada instância como evidência.

/// <summary>Como UMA instância de política do Exchange se comporta diante do critério.</summary>
public enum ExchangePolicyState
{
    Compliant = 0,
    NonCompliant = 1,

    /// <summary>Valor ausente, ou fora dos valores que o contrato reconhece. Nunca vira aprovação nem reprovação.</summary>
    Indeterminate = 2,

    /// <summary>
    /// A política existe, mas NÃO produz efeito (desabilitada). Não viola o critério e também não o sustenta —
    /// é registrada como evidência com o motivo, e sai da conta de aprovação e de exposição.
    /// </summary>
    Inert = 3,
}

/// <summary>Leitura de UMA instância de política para UM critério: o estado e o valor observado, em palavras.</summary>
public sealed record ExchangePolicyReading(ExchangePolicyState State, string Observed)
{
    public static ExchangePolicyReading Compliant(string observed) => new(ExchangePolicyState.Compliant, observed);
    public static ExchangePolicyReading NonCompliant(string observed) => new(ExchangePolicyState.NonCompliant, observed);
    public static ExchangePolicyReading Inert(string observed) => new(ExchangePolicyState.Inert, observed);

    public static ExchangePolicyReading Unknown() => new(ExchangePolicyState.Indeterminate, "não informado pela fonte");

    public static ExchangePolicyReading Unrecognized(string value) =>
        new(ExchangePolicyState.Indeterminate, "valor não reconhecido (" + value + ")");

    /// <summary>Atalho para critérios booleanos: o valor esperado decide o estado.</summary>
    public static ExchangePolicyReading Flag(bool? value, bool expected, string whenTrue = "Sim", string whenFalse = "Não") =>
        value is null
            ? Unknown()
            : new(value.Value == expected ? ExchangePolicyState.Compliant : ExchangePolicyState.NonCompliant,
                value.Value ? whenTrue : whenFalse);
}

/// <summary>Alcance de uma política, já resolvido: quantas caixas a declaram, ou por que a contagem não vale.</summary>
public sealed record ExchangePolicyReachSource(
    Func<IExchangePolicyDocument, int> CountOf,
    bool Demonstrated,
    string? MissingReason,
    int MailboxTotal,
    bool ListComplete,
    int MailboxesWithoutDeclaration)
{
    /// <summary>Alcance não demonstrado, com o motivo (leitura ausente ou não concluída).</summary>
    public static ExchangePolicyReachSource NotDemonstrated(string reason) =>
        new(_ => 0, false, reason, 0, false, 0);
}

public static class ExchangePolicyReach
{
    /// <summary>
    /// Avalia um critério em TODAS as instâncias de um tipo de política e devolve o veredito com os objetos que
    /// o sustentam. <paramref name="read"/> é a única parte específica do controle.
    /// </summary>
    /// <param name="settingId">Identificador estável da configuração lida (ex.: "OwaMailboxPolicy/PersonalAccountsEnabled").</param>
    /// <param name="settingLabel">Nome legível da configuração, o mesmo em tela, HTML, CSV e PDF.</param>
    /// <param name="expected">O que se espera encontrar, em palavras.</param>
    public static KnightControlOutcome Evaluate<TPolicy>(
        KnightEvaluationContext context,
        ExchangePolicyReachSource reach,
        string settingId,
        string settingLabel,
        string expected,
        Func<TPolicy, ExchangePolicyReading> read,
        string exposedEvidence,
        string passedEvidence)
        where TPolicy : class, IExchangePolicyDocument
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reach);

        var policies = context.Configuration.Read<TPolicy>();
        if (!policies.Collected)
            return KnightControlOutcome.NotEvaluated(policies.MissingReason ?? "as políticas do Exchange Online não foram coletadas nesta aquisição.");
        if (policies.Items.Count == 0)
            return KnightControlOutcome.NotEvaluated(
                "a coleta concluiu sem devolver nenhuma política deste tipo — sem instância alguma não há o que avaliar.");

        var instances = policies.Items
            .Select((p, index) => new Instance(p, Interpret(p, read), index))
            .OrderBy(x => x.Policy.IsDefault == true ? 0 : ExchangePolicyIdentities.IsIdentified(x.Policy.Identity) ? 1 : 2)
            .ThenBy(x => x.Policy.Name ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Index)
            .ToList();

        var failing = instances.Where(x => x.Reading.State == ExchangePolicyState.NonCompliant).ToList();
        var unreadable = instances.Where(x => x.Reading.State == ExchangePolicyState.Indeterminate).ToList();
        var evidence = instances
            .Where(x => x.Reading.State != ExchangePolicyState.NonCompliant)
            .Select(x => Evidence(x, settingId, settingLabel, expected, reach))
            .ToList();

        if (failing.Count > 0)
        {
            var affected = failing.Select(x => Affected(x, settingId, settingLabel, expected, reach)).ToList();
            var limitation = Limitation(reach, unreadable, settingLabel);
            return KnightControlOutcome.Exposed(
                exposedEvidence + " " + Failing(failing, reach),
                affected, evidence, complete: unreadable.Count == 0 && reach.Demonstrated, limitation: limitation);
        }

        if (unreadable.Count > 0)
            return KnightControlOutcome.NotEvaluated(
                Unreadable(unreadable, settingLabel)
                + " Aprovar o ambiente exigiria supor o valor dessas instâncias.",
                evidence);

        if (!instances.Any(x => x.Policy.IsDefault == true))
            return KnightControlOutcome.NotEvaluated(
                "a coleta não identificou qual destas políticas é a PADRÃO da organização; sem ela não é possível "
                + "afirmar o que vale para as caixas de correio que não declaram política própria.",
                evidence);

        return KnightControlOutcome.Passed(
            passedEvidence + " " + Scope(instances, reach), evidence);
    }

    /// <summary>UMA instância de política na coleta, com a posição em que chegou.</summary>
    private sealed record Instance(IExchangePolicyDocument Policy, ExchangePolicyReading Reading, int Index);

    /// <summary>
    /// Leitura do critério numa instância, com a regra da IDENTIFICAÇÃO AUSENTE por cima — a mesma do bloco do
    /// Teams: valor que VIOLA continua violando (o defeito é um fato demonstrado), valor que ATENDE vira
    /// indeterminado (um registro que a fonte não identificou não diz a quem se aplica).
    /// </summary>
    private static ExchangePolicyReading Interpret<TPolicy>(TPolicy policy, Func<TPolicy, ExchangePolicyReading> read)
        where TPolicy : class, IExchangePolicyDocument
    {
        var reading = read(policy);
        if (ExchangePolicyIdentities.IsIdentified(policy.Identity) || !string.IsNullOrWhiteSpace(policy.Name))
            return reading;
        return reading.State is ExchangePolicyState.NonCompliant or ExchangePolicyState.Indeterminate
            ? reading
            : new ExchangePolicyReading(ExchangePolicyState.Indeterminate,
                reading.Observed + ", em registro que a coleta devolveu SEM identificação de política");
    }

    // ---- Texto de alcance ------------------------------------------------------------------------------

    /// <summary>Alcance de UMA política, em palavras, com o que a coleta tem — nunca mais do que isso.</summary>
    private static string ReachText(IExchangePolicyDocument policy, ExchangePolicyReachSource reach)
    {
        var isDefault = policy.IsDefault == true
            ? "É a política PADRÃO: vale para toda caixa de correio que não declare outra. "
            : "";

        if (!reach.Demonstrated)
            return isDefault + "Alcance não demonstrado: "
                + (reach.MissingReason ?? "a enumeração de caixas de correio não foi concluída nesta coleta.");

        var count = reach.CountOf(policy);
        var universe = reach.ListComplete
            ? $"de {N(reach.MailboxTotal)} caixa(s) lida(s)"
            : $"de {N(reach.MailboxTotal)} caixa(s) lidas antes do teto da enumeração — a leitura NÃO cobre a organização inteira";

        var undeclared = reach.MailboxesWithoutDeclaration > 0
            ? $" {N(reach.MailboxesWithoutDeclaration)} caixa(s) não declararam política deste tipo e não foram atribuídas a nenhuma."
            : "";

        return isDefault
            + (count == 0
                ? $"Alcance: nenhuma das caixas lidas declara esta política ({universe})."
                : $"Alcance: {N(count)} caixa(s) de correio declaram esta política ({universe}).")
            + undeclared;
    }

    private static KnightIndicatorObject Evidence(
        Instance x, string settingId, string settingLabel, string expected, ExchangePolicyReachSource reach) =>
        KnightObjects.Evidence(KnightAffectedObjectKind.Policy, PolicyId(settingId, x),
            ExchangePolicyIdentities.Label(x.Policy),
            $"Encontrado: {x.Reading.Observed}. Esperado: {expected}. " + ReachText(x.Policy, reach),
            $"{settingLabel}: {x.Reading.Observed}");

    private static KnightIndicatorObject Affected(
        Instance x, string settingId, string settingLabel, string expected, ExchangePolicyReachSource reach) =>
        KnightObjects.Affected(KnightAffectedObjectKind.Policy, PolicyId(settingId, x),
            ExchangePolicyIdentities.Label(x.Policy), null,
            $"Encontrado: {x.Reading.Observed}. Esperado: {expected}. " + ReachText(x.Policy, reach),
            observed: $"{settingLabel}: {x.Reading.Observed}");

    /// <summary>
    /// Identificador do objeto: a configuração lida MAIS a política onde ela foi lida. Sem identificação, entra
    /// a POSIÇÃO na coleta — dois registros não identificados continuam sendo dois objetos distintos.
    /// </summary>
    private static string PolicyId(string settingId, Instance x) =>
        settingId + "@" + (ExchangePolicyIdentities.Key(x.Policy)
            ?? "sem-identificacao-" + x.Index.ToString(CultureInfo.InvariantCulture));

    private static string Failing(IReadOnlyList<Instance> failing, ExchangePolicyReachSource reach)
    {
        var names = failing.Select(f => ExchangePolicyIdentities.Label(f.Policy)).ToList();
        var joined = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " e " + names[^1];
        var head = failing.Count == 1 ? "Encontrado em 1 política: " : $"Encontrado em {N(failing.Count)} políticas: ";

        var reached = reach.Demonstrated ? failing.Sum(f => reach.CountOf(f.Policy)) : -1;
        var scope = failing.Any(f => f.Policy.IsDefault == true)
            ? " Como a política PADRÃO está entre elas, a condição vale para toda caixa de correio que não declare outra."
            : reached >= 0
                ? $" As caixas alcançadas por essas políticas somam {N(reached)} na enumeração desta coleta."
                : " O alcance dessas políticas não pôde ser demonstrado nesta coleta.";

        return head + joined + "." + scope;
    }

    private static string Scope(IReadOnlyList<Instance> instances, ExchangePolicyReachSource reach)
    {
        var inert = instances.Count(x => x.Reading.State == ExchangePolicyState.Inert);
        var head = instances.Count == 1
            ? "Verificado na única política deste tipo nesta coleta."
            : $"Verificado em {N(instances.Count)} política(s) deste tipo (a padrão e as demais).";
        var inertText = inert > 0
            ? $" {N(inert)} delas não produz efeito no ambiente e foi registrada como evidência, sem sustentar a aprovação."
            : "";
        var reachText = reach.Demonstrated
            ? reach.ListComplete
                ? $" A enumeração de caixas de correio usada para o alcance terminou ({N(reach.MailboxTotal)} caixa(s))."
                : $" ⚠️ A enumeração de caixas de correio atingiu o teto ({N(reach.MailboxTotal)} caixa(s) lidas): o alcance informado não cobre a organização inteira."
            : " O alcance das políticas não pôde ser demonstrado nesta coleta: "
              + (reach.MissingReason ?? "a enumeração de caixas de correio não foi concluída.");
        return head + inertText + reachText;
    }

    private static string Limitation(
        ExchangePolicyReachSource reach, IReadOnlyList<Instance> unreadable, string settingLabel)
    {
        var parts = new List<string>();
        if (unreadable.Count > 0) parts.Add(Unreadable(unreadable, settingLabel));
        if (!reach.Demonstrated)
            parts.Add("O alcance das políticas não pôde ser demonstrado: "
                + (reach.MissingReason ?? "a enumeração de caixas de correio não foi concluída."));
        else if (!reach.ListComplete)
            parts.Add($"A enumeração de caixas de correio atingiu o teto ({N(reach.MailboxTotal)} lidas): "
                + "as contagens de alcance não cobrem a organização inteira.");
        return parts.Count == 0 ? "" : string.Join(" ", parts);
    }

    private static string Unreadable(IReadOnlyList<Instance> unreadable, string settingLabel) =>
        $"{N(unreadable.Count)} política(s) não puderam ser interpretadas para “{settingLabel}”: "
        + string.Join(", ", unreadable.Select(u => ExchangePolicyIdentities.Label(u.Policy) + " (" + u.Reading.Observed + ")"))
        + ".";

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
}
