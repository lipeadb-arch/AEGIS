using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Domain;

namespace AegisScore.Application.Knight;

/// <summary>
/// Desfecho determinístico da regra de UM indicador sobre o snapshot: veredito, evidência factual e a
/// quantidade de objetos afetados. É o que a regra do catálogo produz — os metadados (título, severidade,
/// mapeamentos, recomendação) vêm da <see cref="KnightIndicatorDefinition"/>, não da regra.
/// </summary>
public sealed record KnightIndicatorOutcome(
    KnightIndicatorStatus Status,
    string Evidence,
    int AffectedObjectCount);

/// <summary>
/// Definição versionada e ORIGINAL de um indicador do AEGIS KNIGHT. Concentra o texto próprio, a severidade,
/// os mapeamentos (NIST sempre; MITRE só quando defensável), a recomendação curta e a REGRA determinística.
/// Textos autorais — nada é copiado de relatórios de referência de terceiros.
/// </summary>
/// <param name="Id">ID estável (ex.: "AK-ENTRA-001").</param>
/// <param name="Version">Versão do indicador dentro do catálogo.</param>
/// <param name="Title">Título original AEGIS.</param>
/// <param name="Category">Dimensão de identidade avaliada.</param>
/// <param name="Severity">Severidade (régua única do produto).</param>
/// <param name="NistCodes">Códigos NIST CSF 2.0 endereçados (projeção informativa).</param>
/// <param name="MitreTechniques">Técnicas MITRE ATT&amp;CK — só quando fundamentadas; vazio caso contrário.</param>
/// <param name="Recommendation">Recomendação curta e determinística.</param>
/// <param name="ExpectedEvidence">Descrição da evidência que a regra espera encontrar no snapshot.</param>
/// <param name="Rule">Regra PURA que classifica o snapshot no desfecho do indicador.</param>
public sealed record KnightIndicatorDefinition(
    string Id,
    string Version,
    string Title,
    KnightIndicatorCategory Category,
    SeverityLevel Severity,
    IReadOnlyList<string> NistCodes,
    IReadOnlyList<string> MitreTechniques,
    string Recommendation,
    string ExpectedEvidence,
    Func<KnightPostureSnapshot, KnightIndicatorOutcome> Rule);

/// <summary>Indicador já avaliado: a definição (metadados) + o desfecho determinístico do snapshot.</summary>
public sealed record KnightEvaluatedIndicator(
    KnightIndicatorDefinition Definition,
    KnightIndicatorStatus Status,
    string Evidence,
    int AffectedObjectCount);

/// <summary>
/// Catálogo ORIGINAL e VERSIONADO do AEGIS KNIGHT (v1). Cinco indicadores de exposição de identidade
/// (AK-ENTRA-001..005) com regras determinísticas e limiares CENTRALIZADOS aqui — nunca espalhados pelo
/// controller ou pelo frontend. Textos próprios; nenhum nome, descrição, fórmula ou UUID de terceiro.
/// </summary>
public static class KnightCatalog
{
    /// <summary>Versão do catálogo — carimbada na execução para rastreabilidade do veredito.</summary>
    public const string Version = "ak-knight-v1";

    // ---- Limiares centralizados (única fonte da verdade dos números da regra) ----

    /// <summary>Teto de contas privilegiadas aceitável pelo menor privilégio (acima disto, exposição).</summary>
    public const int MaxPrivilegedAccounts = 10;

    /// <summary>Janela (dias) que qualifica um convidado como inativo.</summary>
    public const int InactiveGuestWindowDays = 30;

    private static KnightIndicatorOutcome Passed(string evidence) =>
        new(KnightIndicatorStatus.Passed, evidence, 0);

    private static KnightIndicatorOutcome Exposed(string evidence, int affected) =>
        new(KnightIndicatorStatus.Exposed, evidence, affected);

    private static KnightIndicatorOutcome Mitigated(string evidence, int affected) =>
        new(KnightIndicatorStatus.Mitigated, evidence, affected);

    /// <summary>Os cinco indicadores da primeira vertical. Ordem estável = ordem de exibição por padrão.</summary>
    public static IReadOnlyList<KnightIndicatorDefinition> Indicators { get; } = new[]
    {
        // AK-ENTRA-001 — Contas privilegiadas sem MFA efetivo.
        new KnightIndicatorDefinition(
            Id: "AK-ENTRA-001",
            Version: "1",
            Title: "Contas privilegiadas sem autenticação multifator efetiva",
            Category: KnightIndicatorCategory.PrivilegedAccess,
            Severity: SeverityLevel.Critical,
            NistCodes: new[] { "PR.AA-01", "PR.AA-03" },
            MitreTechniques: new[] { "T1078 · Valid Accounts", "T1078.004 · Cloud Accounts" },
            Recommendation: "Exigir MFA resistente a phishing em todas as contas privilegiadas e bloquear o acesso privilegiado sem segundo fator.",
            ExpectedEvidence: "Número de contas privilegiadas cuja verificação de segundo fator não está efetivamente aplicada.",
            Rule: s => s.PrivilegedAccountsWithoutMfa > 0
                ? Exposed(
                    $"{s.PrivilegedAccountsWithoutMfa} de {s.TotalPrivilegedAccounts} conta(s) privilegiada(s) sem MFA efetivo.",
                    s.PrivilegedAccountsWithoutMfa)
                : Passed($"Todas as {s.TotalPrivilegedAccounts} conta(s) privilegiada(s) com MFA efetivo.")),

        // AK-ENTRA-002 — Quantidade excessiva de contas privilegiadas.
        new KnightIndicatorDefinition(
            Id: "AK-ENTRA-002",
            Version: "1",
            Title: "Volume excessivo de contas privilegiadas",
            Category: KnightIndicatorCategory.IdentityGovernance,
            Severity: SeverityLevel.High,
            NistCodes: new[] { "PR.AA-05", "GV.RR-02" },
            MitreTechniques: new[] { "T1078 · Valid Accounts" },
            Recommendation: "Reduzir o número de contas privilegiadas ao mínimo necessário e adotar elevação just-in-time.",
            ExpectedEvidence: $"Total de contas privilegiadas comparado ao teto de menor privilégio ({MaxPrivilegedAccounts}).",
            Rule: s => s.TotalPrivilegedAccounts > MaxPrivilegedAccounts
                ? Exposed(
                    $"{s.TotalPrivilegedAccounts} contas privilegiadas excedem o teto de menor privilégio ({MaxPrivilegedAccounts}).",
                    s.TotalPrivilegedAccounts)
                : Passed(
                    $"{s.TotalPrivilegedAccounts} contas privilegiadas dentro do teto de menor privilégio ({MaxPrivilegedAccounts}).")),

        // AK-ENTRA-003 — Contas privilegiadas com mailbox (superfície de phishing sobre o admin).
        new KnightIndicatorDefinition(
            Id: "AK-ENTRA-003",
            Version: "1",
            Title: "Contas privilegiadas com caixa de correio ativa",
            Category: KnightIndicatorCategory.PrivilegedAccess,
            Severity: SeverityLevel.Medium,
            NistCodes: new[] { "PR.AA-01" },
            MitreTechniques: new[] { "T1566 · Phishing" },
            Recommendation: "Separar contas administrativas de caixas de correio; usar identidades dedicadas sem e-mail para tarefas privilegiadas.",
            ExpectedEvidence: "Número de contas privilegiadas que possuem caixa de correio ativa.",
            Rule: s => s.PrivilegedAccountsWithMailbox > 0
                ? Exposed(
                    $"{s.PrivilegedAccountsWithMailbox} conta(s) privilegiada(s) com caixa de correio ativa — superfície de phishing sobre o administrador.",
                    s.PrivilegedAccountsWithMailbox)
                : Passed("Nenhuma conta privilegiada com caixa de correio ativa.")),

        // AK-ENTRA-004 — Convidados inativos além da janela definida.
        new KnightIndicatorDefinition(
            Id: "AK-ENTRA-004",
            Version: "1",
            Title: "Contas de convidado inativas além da janela definida",
            Category: KnightIndicatorCategory.GuestAccess,
            Severity: SeverityLevel.Medium,
            NistCodes: new[] { "PR.AA-01", "GV.RR-02" },
            MitreTechniques: Array.Empty<string>(),
            Recommendation: "Revisar e desativar contas de convidado sem uso além da janela; automatizar a expiração de acesso de terceiros.",
            ExpectedEvidence: $"Número de convidados inativos há mais de {InactiveGuestWindowDays} dias.",
            Rule: s => s.InactiveGuestAccountsOverWindow > 0
                ? Exposed(
                    $"{s.InactiveGuestAccountsOverWindow} convidado(s) inativo(s) há mais de {s.InactiveGuestWindowDays} dias — acesso de terceiros esquecido.",
                    s.InactiveGuestAccountsOverWindow)
                : Passed($"Nenhum convidado inativo além de {s.InactiveGuestWindowDays} dias.")),

        // AK-ENTRA-005 — Contas técnicas isentas de MFA sem controle compensatório tecnicamente comprovado.
        new KnightIndicatorDefinition(
            Id: "AK-ENTRA-005",
            Version: "1",
            Title: "Contas técnicas isentas de MFA sem controle compensatório comprovado",
            Category: KnightIndicatorCategory.ServiceAccounts,
            Severity: SeverityLevel.High,
            NistCodes: new[] { "PR.AA-01" },
            MitreTechniques: new[] { "T1078 · Valid Accounts" },
            Recommendation: "Migrar contas de serviço para identidades gerenciadas/credenciais rotacionadas; comprovar tecnicamente qualquer isenção por controle compensatório.",
            ExpectedEvidence: "Contas técnicas/de serviço isentas de MFA e a presença (ou não) de um controle compensatório COMPROVADO no snapshot.",
            Rule: s =>
            {
                var count = s.MfaExemptServiceAccounts.Count;
                if (count == 0)
                    return Passed("Nenhuma conta técnica isenta de MFA.");

                // Mitigado EXIGE evidência técnica no snapshot cobrindo contas de serviço. Um controle apenas
                // declarado (não comprovado) NÃO conta — a exposição permanece (fail-closed).
                var proven = s.CompensatingControls.Any(c =>
                    c.TechnicallyProven && c.CoversCategory == KnightIndicatorCategory.ServiceAccounts);

                return proven
                    ? Mitigated(
                        $"{count} conta(s) técnica(s) isenta(s) de MFA, com controle compensatório tecnicamente comprovado no snapshot.",
                        count)
                    : Exposed(
                        $"{count} conta(s) técnica(s) isenta(s) de MFA SEM controle compensatório comprovado.",
                        count);
            }),
    };
}

/// <summary>
/// Motor determinístico PURO que aplica as regras do <see cref="KnightCatalog"/> a um snapshot e produz um
/// resultado por indicador. Sem EF, sem rede, sem IA — testável isoladamente. Uma regra que lance é
/// classificada como <see cref="KnightIndicatorStatus.Error"/> (nunca aprovada por falha, nunca derruba as demais).
/// </summary>
public static class KnightIndicatorEvaluator
{
    public static IReadOnlyList<KnightEvaluatedIndicator> Evaluate(KnightPostureSnapshot snapshot)
    {
        var results = new List<KnightEvaluatedIndicator>(KnightCatalog.Indicators.Count);
        foreach (var def in KnightCatalog.Indicators)
        {
            try
            {
                var outcome = def.Rule(snapshot);
                results.Add(new KnightEvaluatedIndicator(
                    def, outcome.Status, outcome.Evidence, outcome.AffectedObjectCount));
            }
            catch (Exception ex)
            {
                // Falha de mérito da regra NÃO reprova nem aprova: vira Error (fora do score, reduz cobertura).
                results.Add(new KnightEvaluatedIndicator(
                    def, KnightIndicatorStatus.Error,
                    $"Não foi possível avaliar o indicador: {ex.GetType().Name}.", 0));
            }
        }
        return results;
    }
}
