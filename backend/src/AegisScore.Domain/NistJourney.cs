using System;
using System.Collections.Generic;

namespace AegisScore.Domain;

// ============================================================================
//  [AEGIS-NIST-JOURNEY-02] Jornada completa do AEGIS NIST
// ============================================================================
// Rodadas (ciclos) da mesma avaliação, procedimentos de avaliação (examinar, entrevistar, testar), achados acionáveis e
// a trilha de alterações. Organização funcional inspirada no CSF Profile (avaliações → observações por subcategoria →
// métodos e status de teste → achados → planos de remediação → trilha), reconstruída sobre o modelo do AEGIS:
// PostgreSQL, tenant fail-closed, autor da sessão, concorrência pela versão e nada guardado só no navegador.
//
// Três separações que estas entidades existem para sustentar:
//   • procedimento PLANEJADO ≠ resultado OBSERVADO: escolher "testar" não comprova que o teste aconteceu;
//   • conteúdo herdado ou importado ≠ revisão humana: só a gravação humana confirma uma avaliação;
//   • concluir um plano ≠ mudar maturidade: a reavaliação é um ato separado, fundamentado.

/// <summary>Período de uma rodada. Nenhum é obrigatório: o trimestre é só uma das opções.</summary>
public enum NistCyclePeriodKind
{
    Monthly = 0,
    Quarterly = 1,

    /// <summary>Período informado livremente (semestre, campanha, auditoria pontual…).</summary>
    Other = 2,
}

public enum NistCycleStatus
{
    /// <summary>Rodada em trabalho: aceita gravações.</summary>
    Open = 0,

    /// <summary>Rodada encerrada: somente leitura até ser reaberta (a reabertura fica na trilha).</summary>
    Closed = 1,
}

/// <summary>Como uma rodada nova aproveita a anterior. O aproveitamento nunca é uma nova revisão humana.</summary>
public enum NistCycleSeedMode
{
    /// <summary>Começa vazia.</summary>
    None = 0,

    /// <summary>A rodada anterior aparece como REFERÊNCIA de leitura em cada subcategoria; nada é copiado.</summary>
    Reference = 1,

    /// <summary>
    /// Níveis, textos e procedimentos planejados viram RASCUNHO na rodada nova, com origem identificada. Rascunho não entra
    /// nas médias até ser confirmado por uma gravação humana; resultados de testes nunca são herdados.
    /// </summary>
    Draft = 2,
}

/// <summary>
/// De onde veio o CONTEÚDO vigente de um registro da avaliação. Só <see cref="Analyst"/> com revisão humana gravada conta
/// como avaliação confirmada.
/// </summary>
public enum NistContentOrigin
{
    /// <summary>Gravado por uma pessoa na tela (o padrão — inclui todo o histórico anterior a este pacote).</summary>
    Analyst = 0,

    /// <summary>Copiado de outra rodada como rascunho; aguarda confirmação humana.</summary>
    CarriedForward = 1,

    /// <summary>Importado por CSV; aguarda confirmação humana.</summary>
    Imported = 2,
}

/// <summary>
/// Uma RODADA da mesma avaliação (mensal, trimestral ou período informado). Avaliações, evidências, procedimentos e achados
/// pertencem a uma rodada: a rodada anterior permanece exatamente como foi registrada quando uma nova começa.
/// </summary>
public class NistAssessmentCycle : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed).</summary>
    public Guid TenantId { get; set; }

    public Guid AssessmentId { get; set; }

    public string Name { get; set; } = "";
    public NistCyclePeriodKind PeriodKind { get; set; } = NistCyclePeriodKind.Other;
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public NistCycleStatus Status { get; set; } = NistCycleStatus.Open;

    /// <summary>Rodada usada como referência ou rascunho na criação desta (nula quando começou vazia).</summary>
    public Guid? SeedFromCycleId { get; set; }

    public NistCycleSeedMode SeedMode { get; set; } = NistCycleSeedMode.None;

    public Guid? CreatedByAccountId { get; set; }
    public string? CreatedByName { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }
    public string? ClosedByName { get; set; }

    /// <summary>Concorrência otimista (encerrar/reabrir/renomear).</summary>
    public int Version { get; set; }
}

/// <summary>Método de avaliação (NIST SP 800-53A): examinar, entrevistar, testar.</summary>
public enum NistTestMethod
{
    Examine = 0,
    Interview = 1,
    Test = 2,
}

/// <summary>Andamento de um procedimento. "Planejado" não afirma nada sobre o resultado.</summary>
public enum NistProcedureStatus
{
    Planned = 0,
    InProgress = 1,

    /// <summary>Realizado — exige data, observação e conclusão.</summary>
    Performed = 2,

    /// <summary>Não realizado — exige o motivo (na observação).</summary>
    NotPerformed = 3,
}

/// <summary>Conclusão de um procedimento REALIZADO, dita pelo avaliador.</summary>
public enum NistProcedureOutcome
{
    /// <summary>A prática observada atende ao que o procedimento verificava.</summary>
    Satisfactory = 0,

    /// <summary>Atende em parte (com ressalvas registradas na observação).</summary>
    PartiallySatisfactory = 1,

    /// <summary>Não atende.</summary>
    Unsatisfactory = 2,

    /// <summary>A observação não permitiu concluir.</summary>
    Inconclusive = 3,
}

/// <summary>
/// Um procedimento de avaliação de uma subcategoria numa rodada e escopo: o QUE se planejou verificar (método e
/// procedimento) e, separadamente, o que de fato se observou (andamento, data, observação, conclusão e evidências).
/// </summary>
public class NistTestProcedure : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid AssessmentId { get; set; }
    public Guid CycleId { get; set; }
    public Guid AssessmentScopeId { get; set; }
    public string SubcategoryCode { get; set; } = "";

    public NistTestMethod Method { get; set; }

    /// <summary>O procedimento PLANEJADO (o que será examinado, com quem se conversará, o que será testado).</summary>
    public string Procedure { get; set; } = "";

    public NistProcedureStatus Status { get; set; } = NistProcedureStatus.Planned;

    /// <summary>Conclusão — só existe quando o procedimento foi realizado.</summary>
    public NistProcedureOutcome? Outcome { get; set; }

    /// <summary>O que foi observado (ou, se não realizado, por quê).</summary>
    public string? Observation { get; set; }

    public DateOnly? PerformedOn { get; set; }

    /// <summary>Quem registrou o resultado (autor da sessão), quando houve resultado.</summary>
    public Guid? ResultRecordedByAccountId { get; set; }
    public string? ResultRecordedByName { get; set; }
    public DateTimeOffset? ResultRecordedAt { get; set; }

    /// <summary>Evidências vigentes da MESMA subcategoria, rodada e escopo que sustentam a observação.</summary>
    public List<Guid> EvidenceIds { get; set; } = new();

    public NistContentOrigin ContentOrigin { get; set; } = NistContentOrigin.Analyst;
    public string? OriginNote { get; set; }
    public Guid? SourceProcedureId { get; set; }

    public Guid? CreatedByAccountId { get; set; }
    public string? CreatedByName { get; set; }

    /// <summary>Retirada (o registro permanece para a trilha). Nulo = vigente.</summary>
    public DateTimeOffset? RemovedAt { get; set; }
    public string? RemovedByName { get; set; }

    public int Version { get; set; }
}

/// <summary>Situação do ACHADO (o problema). A situação do TRATAMENTO é a do plano vinculado.</summary>
public enum NistFindingStatus
{
    Open = 0,

    /// <summary>Risco aceito formalmente (com justificativa).</summary>
    RiskAccepted = 1,

    /// <summary>Encerrado (com justificativa — por exemplo, após reavaliação da subcategoria).</summary>
    Closed = 2,
}

/// <summary>Prioridade de tratamento de um achado, justificada pelo analista.</summary>
public enum NistFindingPriority
{
    Low = 0,
    Medium = 1,
    High = 2,
    Urgent = 3,
}

/// <summary>
/// Um ACHADO registrado pelo analista a partir de uma lacuna documentada numa subcategoria. Nunca nasce sozinho de uma
/// diferença entre atual e alvo: uma meta de melhoria não é, por si, falha ou risco. Severidade e prioridade exigem
/// justificativa; o tratamento acontece num plano de ação com origem NIST explícita.
/// </summary>
public class NistFinding : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid AssessmentId { get; set; }
    public Guid CycleId { get; set; }
    public Guid AssessmentScopeId { get; set; }
    public string SubcategoryCode { get; set; } = "";

    /// <summary>O problema, em uma linha.</summary>
    public string Title { get; set; } = "";

    /// <summary>A condição observada (o que foi visto, onde, quando).</summary>
    public string Condition { get; set; } = "";

    /// <summary>Risco fundamentado: o que pode acontecer por causa da condição.</summary>
    public string Risk { get; set; } = "";

    /// <summary>Impacto, no limite do que a condição permite afirmar.</summary>
    public string Impact { get; set; } = "";

    /// <summary>A régua única de severidade do produto (Informational não é aceito num achado).</summary>
    public SeverityLevel Severity { get; set; } = SeverityLevel.Medium;
    public string SeverityRationale { get; set; } = "";

    public NistFindingPriority Priority { get; set; } = NistFindingPriority.Medium;
    public string PriorityRationale { get; set; } = "";

    public string Recommendation { get; set; } = "";

    /// <summary>Evidências vigentes da mesma subcategoria, rodada e escopo citadas pelo achado.</summary>
    public List<Guid> EvidenceIds { get; set; } = new();

    public NistFindingStatus Status { get; set; } = NistFindingStatus.Open;
    public string? StatusNote { get; set; }
    public DateTimeOffset? StatusChangedAt { get; set; }
    public string? StatusChangedByName { get; set; }

    /// <summary>
    /// Contexto de ORIGEM congelado no registro (níveis, lacuna, versão da avaliação, rodada e escopo): a leitura atual da
    /// subcategoria muda; a razão pela qual o achado foi registrado, não.
    /// </summary>
    public string OriginContextJson { get; set; } = "";

    public Guid? CreatedByAccountId { get; set; }
    public string? CreatedByName { get; set; }

    public int Version { get; set; }
}

/// <summary>
/// Uma entrada IMUTÁVEL da trilha da jornada NIST: quem mudou o quê, quando, e os valores anteriores e novos. Cobre
/// avaliações, responsáveis, revisões, evidências, procedimentos, achados, rodadas, importações e publicações (a trilha
/// dos planos de ação continua em <see cref="ActionPlanEvent"/>, agora também com valores anteriores e novos).
/// Append-only: nenhum endpoint atualiza ou remove — e o banco recusa UPDATE/DELETE.
/// </summary>
public class NistAuditEntry : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid AssessmentId { get; set; }
    public Guid? CycleId { get; set; }
    public Guid? AssessmentScopeId { get; set; }
    public string? SubcategoryCode { get; set; }

    /// <summary>Objeto alterado: Assessment, Cycle, Scope, Evaluation, Assignment, Review, Evidence, Procedure, Finding, Plan, Import, Publication.</summary>
    public string Subject { get; set; } = "";
    public Guid? SubjectId { get; set; }

    /// <summary>Ação (Created, Updated, Linked, Removed, Approved, StatusChanged, Published…).</summary>
    public string Action { get; set; } = "";

    /// <summary>Resumo legível, sem segredo nem payload bruto.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Lista JSON de { field, label, from, to } — valores anteriores e novos, truncados.</summary>
    public string? ChangesJson { get; set; }

    public DateTimeOffset At { get; set; }
    public Guid? ActorAccountId { get; set; }
    public string ActorName { get; set; } = "";
}
