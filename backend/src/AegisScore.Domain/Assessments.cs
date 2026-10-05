using System;
using System.Collections.Generic;

namespace AegisScore.Domain;

/// <summary>An assessment campaign for a client against a framework version.</summary>
public class Assessment : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid FrameworkVersionId { get; set; }
    public string Name { get; set; } = "";
    public AssessmentStatus Status { get; set; } = AssessmentStatus.Draft;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    /// <summary>[AEGIS-NIST-JOURNEY-01] Objetivo e contexto da avaliação, em texto livre (opcional).</summary>
    public string? Description { get; set; }

    /// <summary>
    /// [AEGIS-NIST-JOURNEY-01] Versão da metodologia de MATURIDADE (escala 1–5 autoral do AEGIS) com que a avaliação
    /// foi conduzida. Fica gravada na avaliação para que o histórico nunca ligue avaliações de metodologias diferentes
    /// como uma série homogênea. Não confundir com <c>aegis-score-v1</c>, a fórmula do score de postura do tenant.
    /// </summary>
    public string MethodologyVersion { get; set; } = AssessmentMethodology.Version;

    public ICollection<AssessmentScope> Scopes { get; set; } = new List<AssessmentScope>();
}

/// <summary>[AEGIS-NIST-JOURNEY-01] Identificação da metodologia de maturidade usada nas avaliações NIST.</summary>
public static class AssessmentMethodology
{
    /// <summary>Mesmo identificador de <c>aegis_methodology.json</c> (escala CMMI-like 1–5, não os Tiers do NIST).</summary>
    public const string Version = "aegis-methodology-v1";

    public const int MinLevel = 1;
    public const int MaxLevel = 5;
}

/// <summary>
/// A scope = one Process assessed in one Business Unit
/// (the Plano Diretor "Macroatividade": [Assessment] – Processo – Área).
///
/// [AEGIS-NIST-JOURNEY-01] O escopo passa a ter NOME e DESCRIÇÃO (o que está dentro e fora da avaliação). Processo e
/// unidade de negócio tornam-se opcionais: um escopo organizacional ("Matriz e operação em nuvem") não precisa ser
/// reduzido a um par processo × área para ser avaliado; os escopos antigos continuam com os vínculos que tinham.
/// </summary>
public class AssessmentScope : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid AssessmentId { get; set; }
    public Guid? BusinessProcessId { get; set; }
    public Guid? BusinessUnitId { get; set; }
    public ScopeStatus Status { get; set; } = ScopeStatus.NotStarted;

    /// <summary>[AEGIS-NIST-JOURNEY-01] Nome curto do escopo. Vazio nos escopos anteriores a este pacote.</summary>
    public string Name { get; set; } = "";

    /// <summary>[AEGIS-NIST-JOURNEY-01] O que está incluído e excluído (unidades, processos, ambientes).</summary>
    public string? Description { get; set; }

    public ICollection<AssessmentTask> Tasks { get; set; } = new List<AssessmentTask>();
    public ICollection<Answer> Answers { get; set; } = new List<Answer>();
    public ICollection<Evidence> Evidence { get; set; } = new List<Evidence>();
    public ICollection<SubcategoryEvaluation> Evaluations { get; set; } = new List<SubcategoryEvaluation>();
}

/// <summary>A workflow step from the Plano Diretor (kickoff → questionnaire → ... → present).</summary>
public class AssessmentTask : Entity
{
    public Guid AssessmentScopeId { get; set; }
    public AssessmentTaskType Type { get; set; }
    public TaskStatus Status { get; set; } = TaskStatus.Open;
    public string? AssigneeId { get; set; }
    public DateOnly? DueDate { get; set; }
}

/// <summary>A questionnaire question tied to a subcategory, with plain-language guidance.</summary>
public class Question : Entity
{
    public Guid SubcategoryId { get; set; }
    public NistSubcategory? Subcategory { get; set; }
    public string? ThemeGroup { get; set; }          // "INVENTÁRIO", "SOFTWARE E NUVEM"
    public string Text { get; set; } = "";           // "Existe inventário de ativos?"
    public string? Guidance { get; set; }            // "Orientação de resposta" (business language)
    public int Order { get; set; }
    public AnswerType AnswerType { get; set; } = AnswerType.YesNoNa;
}

/// <summary>An answer given for a question within a scope.</summary>
public class Answer : Entity
{
    public Guid AssessmentScopeId { get; set; }
    public Guid QuestionId { get; set; }
    public AnswerValue Value { get; set; } = AnswerValue.Unknown;
    public string? Comment { get; set; }
    public AnswerSource Source { get; set; } = AnswerSource.SelfDeclared;
    public string? RespondedById { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
}

/// <summary>
/// Evidence backing an answer / evaluation: document, link, API signal, screenshot, interview.
///
/// [AEGIS-NIST-JOURNEY-01] Numa avaliação NIST, a evidência é o VÍNCULO entre uma subcategoria avaliada num escopo
/// (<see cref="AssessmentScopeId"/> + <see cref="SubcategoryCode"/>) e a sua origem — documento da biblioteca, achado
/// do KNIGHT, inventário de ativos ou registro do analista. A procedência é CONGELADA no vínculo (origem, data na
/// origem e escopo da coleta): uma nova execução do KNIGHT ou um documento revisado não reescrevem o que sustentou a
/// avaliação. Retirar o vínculo não apaga o registro (<see cref="RemovedAt"/>), para preservar a trilha.
/// </summary>
public class Evidence : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid? AssessmentScopeId { get; set; }
    public string? SubcategoryCode { get; set; }
    public EvidenceType Type { get; set; }
    public EvidenceSource Source { get; set; } = EvidenceSource.Analyst;
    public string? Uri { get; set; }                 // SharePoint / external link
    public string? BlobRef { get; set; }             // stored object key
    public string? AiSummary { get; set; }           // AI extraction of the document
    public string? Hash { get; set; }                // integrity (audit trail)

    /// <summary>Data da evidência NA ORIGEM (conclusão da coleta, data do documento, registro do analista).</summary>
    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.UtcNow;

    // ---- [AEGIS-NIST-JOURNEY-01] Procedência congelada no vínculo ----

    /// <summary>Título legível da evidência.</summary>
    public string? Title { get; set; }

    /// <summary>O que a evidência demonstra (e o que ela NÃO demonstra), nas palavras de quem a vinculou.</summary>
    public string? Notes { get; set; }

    /// <summary>Tipo de origem — decide como a referência é interpretada e que critério de vínculo foi exigido.</summary>
    public EvidenceOriginKind OriginKind { get; set; } = EvidenceOriginKind.Manual;

    /// <summary>Referência estável da origem (id do documento; execução e indicador do KNIGHT…).</summary>
    public string? OriginRef { get; set; }

    /// <summary>Rótulo da origem como apresentado no vínculo ("Microsoft Entra ID · AK-ENTRA-001").</summary>
    public string? OriginLabel { get; set; }

    /// <summary>Escopo da coleta na origem (fonte, modo, completude) — congelado no vínculo.</summary>
    public string? OriginScope { get; set; }

    public Guid? RecordedByAccountId { get; set; }
    public string? RecordedByName { get; set; }

    /// <summary>Retirada do vínculo (o registro permanece para auditoria). Nulo = vínculo vigente.</summary>
    public DateTimeOffset? RemovedAt { get; set; }
    public string? RemovedByName { get; set; }
}

/// <summary>
/// THE CORE RECORD — one assessed subcategory in a scope, with Current vs Target maturity.
/// Mirrors a row of the workbook "Requirements" sheet, plus AI provenance.
///
/// [AEGIS-NIST-JOURNEY-01] Passa a ser <see cref="ITenantOwned"/> (filtro e carimbo fail-closed próprios, como os
/// demais filhos) e ganha o que a jornada do analista registra: responsável, lacunas, risco/impacto, orientação de
/// melhoria, "não se aplica", autor da revisão e um token de concorrência (<see cref="Version"/>). Nível ausente é
/// AUSÊNCIA de avaliação — nunca zero —, e a lacuna só existe quando atual e alvo existem (<see cref="Gap"/>).
/// </summary>
public class SubcategoryEvaluation : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid AssessmentScopeId { get; set; }
    public Guid SubcategoryId { get; set; }
    public NistSubcategory? Subcategory { get; set; }

    public int? CurrentLevel { get; set; }           // 1..5
    public int? CurrentScore { get; set; }           // == CurrentLevel
    public string? CurrentComments { get; set; }

    public int? TargetLevel { get; set; }            // 1..5
    public int? TargetScore { get; set; }
    public string? TargetComments { get; set; }

    public EvaluatedBy EvaluatedBy { get; set; } = EvaluatedBy.Analyst;
    public double? Confidence { get; set; }          // AI confidence 0..1
    public string? Rationale { get; set; }           // AI/analyst justification
    public List<Guid> EvidenceRefs { get; set; } = new();

    public string? ReviewedById { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }

    // ---- [AEGIS-NIST-JOURNEY-01] Registro do analista ----

    /// <summary>O resultado não se aplica ao escopo (com justificativa). Exclui atual/alvo e a lacuna.</summary>
    public bool NotApplicable { get; set; }

    /// <summary>Responsável pela prática na organização (nome ou área).</summary>
    public string? OwnerName { get; set; }

    /// <summary>Lacunas observadas entre a situação atual e o resultado esperado.</summary>
    public string? Gaps { get; set; }

    /// <summary>Risco/impacto da lacuna, quando fundamentado.</summary>
    public string? RiskImpact { get; set; }

    /// <summary>Orientação de melhoria / ação de tratamento proposta.</summary>
    public string? ImprovementGuidance { get; set; }

    /// <summary>Nome de quem registrou a revisão humana vigente (o id fica em <see cref="ReviewedById"/>).</summary>
    public string? ReviewedByName { get; set; }

    /// <summary>Token de concorrência otimista: cresce a cada gravação; escrita com versão antiga é recusada.</summary>
    public int Version { get; set; }

    /// <summary>
    /// Lacuna (alvo − atual) SOMENTE quando os dois níveis existem e o resultado se aplica. Nulo = indeterminada: um
    /// nível ausente não é zero (o cálculo antigo tratava ausência como 0 e produzia lacunas fictícias).
    /// </summary>
    public int? Gap => !NotApplicable && TargetScore is int t && CurrentScore is int c ? t - c : null;
}
