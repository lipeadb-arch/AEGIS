using System;
using System.Collections.Generic;
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
    /// A PRÓXIMA PROVIDÊNCIA, derivada da etapa, do prazo e do DESFECHO da última validação.
    ///
    /// Atraso é dito junto com a etapa, e não no lugar dela: saber que está atrasado sem saber onde o trabalho
    /// parou não ajuda ninguém a destravá-lo.
    ///
    /// O desfecho entra aqui porque "existe validação" não diz nada sozinho: uma nova coleta que ENCERROU a
    /// exposição e uma que não comprovou nada levam a providências opostas. Derivar a frase só da presença de
    /// uma validação faria a tela negar, logo abaixo, a comprovação que ela mesma acabou de exibir.
    /// </summary>
    public static string NextStep(
        ActionPlanStatus status,
        bool isOverdue,
        ActionPlanValidationOutcome? applicableOutcome = null,
        ActionPlanValidationOutcome? latestOutcome = null) => status switch
    {
        ActionPlanStatus.Aberto => isOverdue
            ? "Atrasada e ainda não iniciada — confirmar responsável e repactuar o prazo."
            : "Iniciar a execução com o responsável designado.",
        ActionPlanStatus.EmAndamento => isOverdue
            ? "Em andamento e fora do prazo — repactuar a data e registrar o que já foi feito."
            : "Concluir a execução e registrar o que foi feito.",
        ActionPlanStatus.AguardandoValidacao => AwaitingStep(applicableOutcome, latestOutcome),
        ActionPlanStatus.Concluido => "Encerrada. Nenhuma providência pendente.",
        ActionPlanStatus.Vencido => "Etapa legada 'vencida' — reabrir e repactuar prazo para retomar o acompanhamento.",
        _ => "Sem providência definida.",
    };

    /// <summary>
    /// A providência de quem já relatou execução. Segue a validação APLICÁVEL ao ciclo atual; quando não há
    /// nenhuma, a última validação ainda diz algo útil, e o que ela diz muda a frase:
    ///
    ///   • evidência recusada ou sem melhora → a providência é a mesma, aplicável ou não: obter evidência
    ///     adequada, ou retomar a execução. Trocar isso por "a validação não fala por esta execução" seria
    ///     trocar um diagnóstico exato por um genérico;
    ///   • um desfecho POSITIVO que não se aplica (ciclo anterior, ou coleta anterior ao trabalho relatado) →
    ///     é preciso dizer POR QUE ele não conta, senão a tela parece estar ignorando uma comprovação.
    /// </summary>
    private static string AwaitingStep(
        ActionPlanValidationOutcome? applicable, ActionPlanValidationOutcome? latest)
    {
        if (applicable is { } outcome) return StepForOutcome(outcome);

        return latest switch
        {
            null => "Execução relatada — validar com uma nova avaliação ou registrar evidência de comprovação.",
            ActionPlanValidationOutcome.EvidenceInsufficient or ActionPlanValidationOutcome.NoChangeObserved =>
                StepForOutcome(latest.Value),
            _ =>
                "A validação registrada não fala por esta execução (é de um ciclo anterior, ou apoia-se numa " +
                "coleta anterior ao trabalho relatado) — validar de novo com uma coleta posterior à execução.",
        };
    }

    /// <summary>A providência que cada desfecho pede, dito sem eufemismo.</summary>
    private static string StepForOutcome(ActionPlanValidationOutcome outcome) => outcome switch
    {
        ActionPlanValidationOutcome.ExposureCleared =>
            "Melhora comprovada por nova coleta: o achado deixou de estar exposto. Encerrar a ação.",
        ActionPlanValidationOutcome.ReductionObserved =>
            "Redução comprovada por nova coleta, porém o achado continua exposto. Encerrar esta ação e abrir um " +
            "novo ciclo para o que restou.",
        ActionPlanValidationOutcome.HumanAttested =>
            "Atestação humana registrada — o AEGIS não verificou o ambiente. Encerrar a ação assumindo isso, ou " +
            "comprovar tecnicamente com uma nova coleta.",
        ActionPlanValidationOutcome.NoChangeObserved =>
            "A nova coleta não mostrou melhora neste achado — retomar a execução antes de encerrar.",
        _ => "A última validação não comprovou a correção — apresentar evidência adequada ou executar nova coleta.",
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
    /// <c>true</c> quando o desfecho SUSTENTA o encerramento do ciclo. Ausência de melhora e evidência
    /// insuficiente não sustentam: encerrar sobre elas apresentaria como resolvido um ciclo que nada
    /// comprovou. A atestação humana sustenta — a organização pode decidir com base na palavra de alguém —,
    /// mas continua identificada como atestação em toda leitura, nunca somada à melhora comprovada
    /// (ver <see cref="IsTechnicallyProven"/>), e "redução observada" nunca vira "exposição encerrada".
    /// </summary>
    public static bool SupportsClosure(ActionPlanValidationOutcome outcome) => outcome
        is ActionPlanValidationOutcome.ExposureCleared
        or ActionPlanValidationOutcome.ReductionObserved
        or ActionPlanValidationOutcome.HumanAttested;

    /// <summary>
    /// O que o CICLO ATUAL de uma ação tem efetivamente registrado. É esta base — e não a mera sequência de
    /// cliques em botões de etapa — que decide o que a ação pode fazer a seguir.
    /// </summary>
    /// <param name="HasExecutionInCurrentCycle">Execução relatada DEPOIS do início do ciclo vigente.</param>
    /// <param name="ApplicableOutcome">
    /// Desfecho da validação aplicável ao ciclo atual, se houver. Uma validação de um ciclo encerrado, ou
    /// apoiada numa coleta anterior ao trabalho relatado, NÃO é aplicável e não aparece aqui.
    /// </param>
    public readonly record struct ActionPlanCycleBasis(
        bool HasExecutionInCurrentCycle,
        ActionPlanValidationOutcome? ApplicableOutcome)
    {
        /// <summary>Base vazia — nada registrado. O que uma ação recém-criada tem.</summary>
        public static readonly ActionPlanCycleBasis Empty = new(false, null);

        /// <summary>A base autoriza encerrar o ciclo? Exige execução relatada E validação que a sustente.</summary>
        public bool AllowsClosure =>
            HasExecutionInCurrentCycle && ApplicableOutcome is { } o && SupportsClosure(o);
    }

    /// <summary>
    /// O início do ciclo VIGENTE. Planos legados (e qualquer linha anterior a esta entrega) caem para a
    /// criação — que é, de fato, quando o único ciclo deles começou.
    /// </summary>
    public static DateTimeOffset CycleStartOf(ActionPlan plan) => plan.CycleStartedAt ?? plan.CreatedAt;

    /// <summary>
    /// Esta validação fala pelo CICLO ATUAL? Quatro recusas, todas pelo mesmo motivo de fundo — a validação
    /// precisa ser uma decisão sobre O TRABALHO QUE ESTÁ SENDO ENCERRADO, não um registro qualquer no
    /// histórico da ação:
    ///
    ///   • não há execução relatada no ciclo → não existe execução a validar;
    ///   • a validação é anterior ao início do ciclo → ela julgou um ciclo que já foi encerrado;
    ///   • a validação é anterior ao relato de execução → decidiu sobre um trabalho ainda não relatado;
    ///   • a COLETA usada como evidência é anterior ao relato → pode mostrar mudança real no ambiente, mas
    ///     não mudança produzida por esta execução. Correlação temporal invertida não é causalidade.
    ///
    /// Nada disso apaga a validação: ela permanece na trilha, visível, apenas não autoriza o encerramento.
    /// </summary>
    public static bool IsApplicableToCurrentCycle(
        ActionPlanValidation validation, DateTimeOffset cycleStartedAt, DateTimeOffset? executedAt)
    {
        if (executedAt is not { } executed) return false;
        if (executed < cycleStartedAt) return false;
        if (validation.DecidedAt < cycleStartedAt) return false;
        if (validation.DecidedAt < executed) return false;

        if (validation.Method == ActionPlanValidationMethod.NewAssessment)
        {
            if (validation.PrecedesReportedExecution) return false;
            if (validation.EvidenceCollectedAt is { } collected && collected < executed) return false;
        }
        return true;
    }

    /// <summary>
    /// A base do ciclo atual de um plano, a partir do que está gravado. Autoridade ÚNICA: o serviço a usa
    /// para decidir a transição, a leitura para compor a providência e a fotografia para congelar o estado —
    /// se cada um calculasse a sua, a tela ofereceria um botão que o servidor recusaria.
    /// </summary>
    public static ActionPlanCycleBasis BasisFor(ActionPlan plan, IEnumerable<ActionPlanValidation> validations)
    {
        var cycleStart = CycleStartOf(plan);
        var executed = plan.ExecutedAt;
        var hasExecution = executed is { } e && e >= cycleStart;

        ActionPlanValidation? applicable = null;
        foreach (var v in validations)
        {
            if (!IsApplicableToCurrentCycle(v, cycleStart, executed)) continue;
            if (applicable is null || v.DecidedAt > applicable.DecidedAt) applicable = v;
        }

        return new ActionPlanCycleBasis(hasExecution, applicable?.Outcome);
    }

    /// <summary>
    /// Transições PERMITIDAS da etapa operacional, dado o que o ciclo tem REGISTRADO. O caminho é
    /// Aberto → Em andamento → Aguardando validação → Concluído; voltar atrás é permitido (o trabalho real
    /// volta), e "Vencido" nunca é destino (atraso vem do prazo).
    ///
    /// Duas exigências que existem porque, sem elas, a jornada inteira vira teatro:
    ///
    ///   • "Aguardando validação" significa <em>execução relatada, aguardando comprovação</em>. Chegar lá por
    ///     um clique de etapa, sem relato algum, FABRICARIA a execução — a tela passaria a afirmar um trabalho
    ///     que ninguém descreveu.
    ///   • "Concluído" exige execução relatada NO CICLO ATUAL e uma decisão de validação que sustente o
    ///     encerramento. Sem isso, bastaria clicar duas vezes para apresentar como encerrado um ciclo sem
    ///     nenhuma base registrada — que é exatamente o atalho que este pacote existe para fechar.
    ///
    /// Não há aqui aceite de risco nem encerramento administrativo: a saída honesta de um ciclo sem melhora
    /// é voltar para a execução, ou registrar uma atestação humana — que fica marcada como atestação.
    /// </summary>
    public static bool IsAllowedTransition(ActionPlanStatus from, ActionPlanStatus to, ActionPlanCycleBasis basis)
    {
        if (from == to) return true;
        if (to == ActionPlanStatus.Vencido) return false;   // atraso é derivado do prazo, não uma etapa

        return (from, to) switch
        {
            (ActionPlanStatus.Aberto, ActionPlanStatus.EmAndamento) => true,
            (ActionPlanStatus.Aberto, ActionPlanStatus.AguardandoValidacao) => basis.HasExecutionInCurrentCycle,
            (ActionPlanStatus.EmAndamento, ActionPlanStatus.Aberto) => true,
            (ActionPlanStatus.EmAndamento, ActionPlanStatus.AguardandoValidacao) => basis.HasExecutionInCurrentCycle,
            (ActionPlanStatus.AguardandoValidacao, ActionPlanStatus.EmAndamento) => true,
            (ActionPlanStatus.AguardandoValidacao, ActionPlanStatus.Concluido) => basis.AllowsClosure,
            // Reabertura de uma ação encerrada: o problema pode voltar, e reabrir é melhor do que duplicar.
            (ActionPlanStatus.Concluido, ActionPlanStatus.EmAndamento) => true,
            (ActionPlanStatus.Vencido, ActionPlanStatus.EmAndamento) => true,
            (ActionPlanStatus.Vencido, ActionPlanStatus.Aberto) => true,
            _ => false,
        };
    }

    /// <summary>As etapas alcançáveis a partir da atual, dada a base do ciclo — a lista que a tela oferece.</summary>
    public static IReadOnlyList<ActionPlanStatus> AllowedTransitions(
        ActionPlanStatus from, ActionPlanCycleBasis basis)
    {
        var destinos = new[]
        {
            ActionPlanStatus.Aberto, ActionPlanStatus.EmAndamento,
            ActionPlanStatus.AguardandoValidacao, ActionPlanStatus.Concluido,
        };
        var lista = new List<ActionPlanStatus>(destinos.Length);
        foreach (var to in destinos)
            if (to != from && IsAllowedTransition(from, to, basis)) lista.Add(to);
        return lista;
    }

    /// <summary>
    /// Por que encerrar ainda não está disponível — <c>null</c> quando está. A tela precisa DIZER o que falta;
    /// esconder o botão sem explicação faz a pessoa procurar o defeito no produto em vez de fazer o trabalho.
    /// </summary>
    public static string? ClosureBlockedReason(ActionPlanStatus status, ActionPlanCycleBasis basis)
    {
        if (status == ActionPlanStatus.Concluido) return null;
        if (!basis.HasExecutionInCurrentCycle)
            return "Encerrar exige o relato do que foi feito neste ciclo. Registre a execução primeiro — " +
                   "avançar a etapa não é o mesmo que executar.";
        if (basis.ApplicableOutcome is not { } outcome)
            return "Encerrar exige uma decisão de validação sobre ESTA execução. Valide com uma coleta " +
                   "posterior ao trabalho relatado, ou registre uma atestação humana com evidência.";
        if (!SupportsClosure(outcome))
            return $"A validação aplicável a este ciclo é '{OutcomeLabel(outcome)}' — ela não sustenta o " +
                   "encerramento. Retome a execução e valide de novo.";
        return null;
    }
}
