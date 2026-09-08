using System;

namespace AegisScore.Domain;

// ============================================================================
//  [AEGIS-MVP-PRODUCT-03] Trilha e COMPROVAÇÃO de um plano de ação
// ============================================================================
// O ActionPlan diz o que se pretende fazer e em que etapa está. Estas duas entidades dizem o que
// EFETIVAMENTE aconteceu, e é a distinção entre elas que impede a jornada de mentir:
//
//   • ActionPlanEvent      — a trilha: quem mudou o quê, quando. Inclui o relato de execução.
//   • ActionPlanValidation — a COMPROVAÇÃO: um julgamento explícito, com método, desfecho e evidência.
//
// "Marcar como executado" mora no evento. "Está corrigido" só pode morar na validação — e mesmo lá, o
// desfecho distingue melhora observada de resolução, e prova técnica de atestação humana.

/// <summary>Natureza de uma entrada da trilha de auditoria de um plano de ação.</summary>
public enum ActionPlanEventKind
{
    /// <summary>Plano criado a partir de um achado (ou de um risco).</summary>
    Created = 0,

    /// <summary>Mudança de etapa operacional (Aberto → Em andamento → Aguardando validação → Concluído).</summary>
    StatusChanged = 1,

    /// <summary>Execução RELATADA — o que foi feito, segundo quem fez. Não é comprovação.</summary>
    ExecutionRecorded = 2,

    /// <summary>Uma validação foi registrada, com seu desfecho.</summary>
    ValidationRecorded = 3,

    /// <summary>Campos do plano editados (responsável, área, prazo, texto).</summary>
    Edited = 4,
}

/// <summary>
/// Uma entrada IMUTÁVEL da trilha de auditoria de um plano de ação — tenant-owned. Registra autor, instante
/// e a mudança relevante. Nunca é atualizada nem removida por endpoint operacional: a história de um plano
/// não se reescreve.
/// </summary>
public class ActionPlanEvent : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    public Guid ActionPlanId { get; set; }
    public ActionPlan? ActionPlan { get; set; }

    public ActionPlanEventKind Kind { get; set; }

    /// <summary>Instante da mudança (relógio do servidor).</summary>
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Conta que executou a mudança, quando resolvida do token. Nulo nunca vira "sistema".</summary>
    public Guid? ActorAccountId { get; set; }

    /// <summary>Nome de exibição do autor, como o token o apresentou. Vazio quando o token não o trouxe.</summary>
    public string ActorName { get; set; } = "";

    /// <summary>Etapa anterior — nula quando o evento não é transição de etapa.</summary>
    public ActionPlanStatus? FromStatus { get; set; }

    /// <summary>Etapa resultante — nula quando o evento não é transição de etapa.</summary>
    public ActionPlanStatus? ToStatus { get; set; }

    /// <summary>Descrição curta e sanitizada da mudança (sem segredo, sem payload bruto).</summary>
    public string? Note { get; set; }
}

/// <summary>
/// COMO a validação foi feita. O método viaja junto com o desfecho porque muda radicalmente o peso da
/// afirmação: uma nova coleta é prova técnica; a palavra de uma pessoa é atestação.
/// </summary>
public enum ActionPlanValidationMethod
{
    /// <summary>Nova avaliação KNIGHT comparada com a de origem — prova técnica, sujeita a compatibilidade.</summary>
    NewAssessment = 0,

    /// <summary>
    /// Validação HUMANA com evidência anexada por referência. Vale como registro, e é IDENTIFICADA como tal:
    /// um comentário "feito", um aceite de risco ou um encerramento administrativo NUNCA é apresentado como
    /// correção técnica comprovada.
    /// </summary>
    HumanEvidence = 1,
}

/// <summary>
/// O DESFECHO de uma validação. Deliberadamente NÃO colapsa "melhorou" em "resolvido": contagem menor é
/// redução observada, e afirmar resolução a partir de uma diferença de totais seria inventar a conclusão
/// que o dado não sustenta.
/// </summary>
public enum ActionPlanValidationOutcome
{
    /// <summary>
    /// O achado deixou de estar exposto na nova avaliação, com evidência SUFICIENTE para o indicador.
    /// É a única forma de "resolvido" que a comparação automática pode afirmar.
    /// </summary>
    ExposureCleared = 0,

    /// <summary>A quantidade afetada caiu, mas o achado continua exposto — redução observada, não resolução.</summary>
    ReductionObserved = 1,

    /// <summary>A nova avaliação não mostrou melhora (quantidade igual ou maior).</summary>
    NoChangeObserved = 2,

    /// <summary>
    /// A evidência apresentada NÃO comprova nada sobre este indicador: capacidade necessária ausente ou com
    /// erro, fontes incomparáveis, regras incompatíveis ou ordem temporal inválida. Ausência de prova NUNCA
    /// vira prova de melhora.
    /// </summary>
    EvidenceInsufficient = 3,

    /// <summary>
    /// Atestação HUMANA registrada, com evidência referenciada. Vale como decisão documentada e aparece
    /// SEMPRE identificada como atestação — jamais como correção técnica comprovada.
    /// </summary>
    HumanAttested = 4,
}

/// <summary>
/// Uma validação registrada de um plano de ação — tenant-owned e IMUTÁVEL. Guarda separadamente o método, o
/// desfecho, a EVIDÊNCIA (a avaliação usada como prova, distinta da avaliação de origem do plano) e o que foi
/// efetivamente observado. É esta separação que permite ao relatório apresentar, sem se contradizer, três
/// coisas distintas: o estado do plano, o resultado observado no achado e o método de validação.
/// </summary>
public class ActionPlanValidation : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    public Guid ActionPlanId { get; set; }
    public ActionPlan? ActionPlan { get; set; }

    /// <summary>Indicador validado — denormalizado para leitura por achado sem join extra.</summary>
    public string IndicatorId { get; set; } = "";

    public ActionPlanValidationMethod Method { get; set; }
    public ActionPlanValidationOutcome Outcome { get; set; }

    /// <summary>
    /// Avaliação usada como EVIDÊNCIA da validação — NUNCA a avaliação de origem do plano. Nula quando o
    /// método é atestação humana.
    /// </summary>
    public Guid? ValidationRunId { get; set; }

    /// <summary>Referência da evidência humana (chamado, documento, registro), sanitizada. Nula na comparação automática.</summary>
    public string? EvidenceReference { get; set; }

    /// <summary>
    /// Instante da COLETA usada como evidência — não o instante em que alguém clicou em validar. É por ele
    /// que se decide se a evidência descreve o mundo DEPOIS do trabalho relatado. Nulo na atestação humana.
    /// </summary>
    public DateTimeOffset? EvidenceCollectedAt { get; set; }

    /// <summary>
    /// <c>true</c> quando a coleta usada como evidência é ANTERIOR ao relato de execução desta ação. A
    /// observação continua verdadeira — a exposição pode de fato ter caído —, mas ela não pode ser atribuída
    /// a um trabalho que ainda não tinha sido relatado. Uma validação assim fica registrada e NÃO autoriza a
    /// conclusão do ciclo: correlação no tempo não é causalidade.
    /// </summary>
    public bool PrecedesReportedExecution { get; set; }

    /// <summary>Quantidade afetada na avaliação de ORIGEM — o ponto de partida. Nula quando não aplicável.</summary>
    public int? ObservedBefore { get; set; }

    /// <summary>Quantidade afetada na avaliação de EVIDÊNCIA. Nula quando não aplicável.</summary>
    public int? ObservedAfter { get; set; }

    /// <summary>
    /// Objetos que ESTAVAM na lista da origem e NÃO estão na da evidência — só preenchido quando as DUAS
    /// listas foram preservadas e declaradas completas. Sem isso, o produto diz quantos saíram, jamais quais.
    /// </summary>
    public int? ObjectsNoLongerPresent { get; set; }

    /// <summary>
    /// TRUE quando a conclusão se apoia na comparação dos CONJUNTOS preservados (e não apenas de totais).
    /// FALSE é honesto e comum: significa "houve variação de quantidade", não "estes objetos foram corrigidos".
    /// </summary>
    public bool ComparedBySets { get; set; }

    /// <summary>Justificativa determinística do desfecho, em linguagem de gestão. Sem PII, sem segredo.</summary>
    public string Rationale { get; set; } = "";

    public DateTimeOffset DecidedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Conta que registrou a validação, quando resolvida do token.</summary>
    public Guid? DecidedByAccountId { get; set; }

    /// <summary>Nome de exibição de quem registrou, como o token o apresentou.</summary>
    public string DecidedByName { get; set; } = "";
}
