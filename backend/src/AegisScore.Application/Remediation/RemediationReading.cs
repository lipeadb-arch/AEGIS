using System;
using AegisScore.Domain;

namespace AegisScore.Application.Remediation;

// ============================================================================
//  [AEGIS-MVP-PRODUCT-03] Leitura em linguagem de gestão de um plano de ação
// ============================================================================
// Funções PURAS, com uma autoridade única, usadas em três lugares que não podem divergir: a lista de planos
// na Central de Prioridades, o congelamento na fotografia e o relatório publicado. Se cada um escrevesse a
// sua própria frase, o PDF diria uma coisa e a tela outra sobre o mesmo plano.

/// <summary>Textos determinísticos derivados do estado de um plano — nunca gerados por IA.</summary>
public static class RemediationReading
{
    /// <summary>
    /// A PRÓXIMA PROVIDÊNCIA, derivada da etapa e do prazo. Atraso é dito junto com a etapa, e não no lugar
    /// dela: saber que está atrasado sem saber onde parou não ajuda ninguém a destravar o trabalho.
    /// </summary>
    public static string NextStep(ActionPlanStatus status, bool isOverdue, bool hasValidation) => status switch
    {
        ActionPlanStatus.Aberto => isOverdue
            ? "Atrasada e ainda não iniciada — confirmar responsável e repactuar o prazo."
            : "Iniciar a execução com o responsável designado.",
        ActionPlanStatus.EmAndamento => isOverdue
            ? "Em andamento e fora do prazo — repactuar a data e registrar o que já foi feito."
            : "Concluir a execução e registrar o que foi feito.",
        ActionPlanStatus.AguardandoValidacao => hasValidation
            ? "Execução relatada; a última validação não comprovou a correção — reavaliar ou executar nova coleta."
            : "Execução relatada — validar com uma nova avaliação ou registrar evidência de comprovação.",
        ActionPlanStatus.Concluido => "Encerrada. Nenhuma providência pendente.",
        ActionPlanStatus.Vencido => "Etapa legada 'vencida' — reabrir e repactuar prazo para retomar o acompanhamento.",
        _ => "Sem providência definida.",
    };

    /// <summary>Rótulo pt-BR da etapa operacional (o relatório e a tela usam o mesmo).</summary>
    public static string StatusLabel(ActionPlanStatus status) => status switch
    {
        ActionPlanStatus.Aberto => "Aberta",
        ActionPlanStatus.EmAndamento => "Em andamento",
        ActionPlanStatus.AguardandoValidacao => "Aguardando validação",
        ActionPlanStatus.Concluido => "Concluída",
        ActionPlanStatus.Vencido => "Vencida (legado)",
        _ => status.ToString(),
    };

    /// <summary>
    /// Rótulo pt-BR do desfecho de validação. Nenhum deles diz "resolvido" sem qualificação: cada um carrega,
    /// no próprio nome, o limite do que a evidência sustenta.
    /// </summary>
    public static string OutcomeLabel(ActionPlanValidationOutcome outcome) => outcome switch
    {
        ActionPlanValidationOutcome.ExposureCleared => "Exposição encerrada na nova avaliação",
        ActionPlanValidationOutcome.ReductionObserved => "Redução observada (achado ainda exposto)",
        ActionPlanValidationOutcome.NoChangeObserved => "Sem melhora observada",
        ActionPlanValidationOutcome.EvidenceInsufficient => "Evidência insuficiente para comprovar",
        ActionPlanValidationOutcome.HumanAttested => "Atestação humana (não é comprovação técnica)",
        _ => outcome.ToString(),
    };

    /// <summary>Rótulo pt-BR do método — o que dá (ou tira) peso à afirmação.</summary>
    public static string MethodLabel(ActionPlanValidationMethod method) => method switch
    {
        ActionPlanValidationMethod.NewAssessment => "Comparação com nova avaliação",
        ActionPlanValidationMethod.HumanEvidence => "Atestação humana com evidência referenciada",
        _ => method.ToString(),
    };

    /// <summary>
    /// <c>true</c> SOMENTE quando a melhora foi COMPROVADA tecnicamente por uma nova coleta compatível.
    /// Atestação humana, aceite de risco e encerramento administrativo não entram aqui — é esta função que
    /// impede o relatório de somá-los à "melhora efetivamente comprovada".
    /// </summary>
    public static bool IsTechnicallyProven(ActionPlanValidationMethod method, ActionPlanValidationOutcome outcome) =>
        method == ActionPlanValidationMethod.NewAssessment
        && outcome is ActionPlanValidationOutcome.ExposureCleared or ActionPlanValidationOutcome.ReductionObserved;

    /// <summary>
    /// Transições PERMITIDAS da etapa operacional. O caminho feliz é Aberto → Em andamento → Aguardando
    /// validação → Concluído; voltar atrás é permitido (o trabalho real volta), e "Vencido" nunca é destino
    /// (atraso vem do prazo). Concluir a partir de "Aberto" é recusado: nada foi sequer relatado.
    /// </summary>
    public static bool IsAllowedTransition(ActionPlanStatus from, ActionPlanStatus to)
    {
        if (from == to) return true;
        if (to == ActionPlanStatus.Vencido) return false;   // atraso é derivado do prazo, não uma etapa

        return (from, to) switch
        {
            (ActionPlanStatus.Aberto, ActionPlanStatus.EmAndamento) => true,
            (ActionPlanStatus.Aberto, ActionPlanStatus.AguardandoValidacao) => true,
            (ActionPlanStatus.EmAndamento, ActionPlanStatus.Aberto) => true,
            (ActionPlanStatus.EmAndamento, ActionPlanStatus.AguardandoValidacao) => true,
            (ActionPlanStatus.AguardandoValidacao, ActionPlanStatus.EmAndamento) => true,
            (ActionPlanStatus.AguardandoValidacao, ActionPlanStatus.Concluido) => true,
            // Reabertura de uma ação encerrada: o problema pode voltar, e reabrir é melhor do que duplicar.
            (ActionPlanStatus.Concluido, ActionPlanStatus.EmAndamento) => true,
            (ActionPlanStatus.Vencido, ActionPlanStatus.EmAndamento) => true,
            (ActionPlanStatus.Vencido, ActionPlanStatus.Aberto) => true,
            _ => false,
        };
    }
}
