using System;
using System.Collections.Generic;

namespace AegisScore.Domain;

/// <summary>A risk = threat × vulnerability on a process, owned by a BU. (Sistema de Gestão de Riscos)</summary>
public class Risk : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public string Code { get; set; } = "";           // "SEC0001"
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public Guid? BusinessProcessId { get; set; }
    public Guid? BusinessUnitId { get; set; }
    public string? Threats { get; set; }
    public string? Vulnerabilities { get; set; }
    public string? FocalPoint { get; set; }
    public string? ManagerName { get; set; }
    public ProcessClassification Classification { get; set; } = ProcessClassification.Interno;
    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Traceability: the gap or signal that originated this risk.</summary>
    public string? OriginSubcategoryCode { get; set; }
    public Guid? OriginEvidenceId { get; set; }

    /// <summary>
    /// Rastreabilidade (ID.RA): a exposição ativo↔ameaça que ORIGINOU este risco, quando ele é promovido a
    /// partir de um raio de explosão. Nulo para riscos de outra procedência. FK lógica → AssetThreatExposure
    /// (o mapeamento EF/relacional fica para a fase de infraestrutura).
    /// </summary>
    public Guid? OriginExposureId { get; set; }

    public ICollection<RiskEvaluation> Evaluations { get; set; } = new List<RiskEvaluation>();
    public ICollection<ActionPlan> ActionPlans { get; set; } = new List<ActionPlan>();
}

/// <summary>Inherent or residual evaluation: Probability + Impact + ProcessValue (each 1–4).</summary>
public class RiskEvaluation : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }   // denormalizado: defesa em profundidade + stamping automático
    public Guid RiskId { get; set; }
    public RiskPhase Phase { get; set; } = RiskPhase.Inherent;
    public int ProcessValue { get; set; }            // 1..4
    public int Probability { get; set; }             // 1..4
    public int Impact { get; set; }                  // 1..4
    public int RiskScore { get; set; }               // computed (3..12)
    public RiskLevel RiskLevel { get; set; }
    public DateTimeOffset EvaluatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Risk treatment / action plan (Plano de Ação): what/who/how/when + status.
///
/// [AEGIS-MVP-PRODUCT-03] A MENOR adaptação compatível para que uma ação possa nascer de um ACHADO do AEGIS
/// KNIGHT em vez de exigir um registro de risco: <see cref="RiskId"/> passou a ser ANULÁVEL. Não se cria um
/// risco fictício só para satisfazer a FK (isso poluiria o registro de riscos com objetos que ninguém avaliou),
/// e não se cria um segundo sistema genérico de tarefas ao lado deste (duas verdades sobre "o que falta fazer").
///
/// Os planos legados permanecem EXATAMENTE como foram gravados: <see cref="RiskId"/> preenchido, origem KNIGHT
/// nula, versão zero. As consultas existentes fazem INNER JOIN com <c>Risks</c>, então continuam vendo só os
/// planos de risco — uma ação de achado não entra num indicador de risco que ela não representa.
/// </summary>
public class ActionPlan : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }   // denormalizado: defesa em profundidade + stamping automático

    /// <summary>
    /// Risco de origem — ANULÁVEL desde [AEGIS-MVP-PRODUCT-03]. Nulo numa ação nascida de um achado KNIGHT;
    /// preenchido nos planos de tratamento de risco (inclusive todos os legados).
    /// </summary>
    public Guid? RiskId { get; set; }

    public RiskTreatmentType Treatment { get; set; } = RiskTreatmentType.Mitigar;

    /// <summary>[AEGIS-MVP-PRODUCT-03] Título curto da ação. Nulo nos planos legados, que só tinham descrição.</summary>
    public string? Title { get; set; }

    /// <summary>A ação proposta (o "o quê"). Numa ação de achado, começa com o contexto e é editável pelo humano.</summary>
    public string? Description { get; set; }

    public string? ResponsibleArea { get; set; }
    public string? ResponsiblePerson { get; set; }
    public string? HowToImplement { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public ActionPlanStatus Status { get; set; } = ActionPlanStatus.Aberto;
    public DateTimeOffset? CompletedAt { get; set; }

    // ---- [AEGIS-MVP-PRODUCT-03] Origem no AEGIS KNIGHT ----------------------------------------------
    // Registrada EXPLICITAMENTE: tenant (já acima), indicador e a avaliação que originou a ação. A
    // referência de ORIGEM é deliberadamente separada da referência de VALIDAÇÃO (que vive em
    // ActionPlanValidation): confundi-las faria a prova de correção apontar para a própria coleta que
    // revelou o problema.

    /// <summary>Indicador KNIGHT de origem (ex.: "AK-ENTRA-001"). Nulo quando a ação vem de um risco.</summary>
    public string? KnightIndicatorId { get; set; }

    /// <summary>Avaliação KNIGHT que ORIGINOU a ação — nunca a que a valida depois.</summary>
    public Guid? OriginRunId { get; set; }

    /// <summary>Quantidade afetada observada NA ORIGEM — o ponto de partida contra o qual a melhora é medida.</summary>
    public int? OriginAffectedCount { get; set; }

    // ---- [AEGIS-MVP-PRODUCT-03] Execução relatada ---------------------------------------------------
    // "Marcar como executado" é RELATO, não prova. Fica registrado como relato, com autor e data, e a
    // comprovação acontece em ActionPlanValidation.

    /// <summary>O que foi feito, descrito por quem executou. Relato — não comprovação.</summary>
    public string? ExecutionNotes { get; set; }

    /// <summary>Referência à evidência da execução (chamado, documento, registro). Texto sanitizado, sem segredo.</summary>
    public string? ExecutionEvidenceRef { get; set; }

    /// <summary>Instante em que a execução foi relatada.</summary>
    public DateTimeOffset? ExecutedAt { get; set; }

    /// <summary>
    /// Contador de versão para CONCORRÊNCIA OTIMISTA. O cliente devolve a versão que leu; uma atualização
    /// sobre versão diferente é recusada (409) em vez de sobrescrever silenciosamente o trabalho de outra
    /// pessoa. Zero nos planos legados — o valor inicial de quem nunca foi editado por esta superfície.
    /// </summary>
    public int Version { get; set; }

    /// <summary>Trilha de auditoria: quem mudou o quê, e quando.</summary>
    public ICollection<ActionPlanEvent> Events { get; set; } = new List<ActionPlanEvent>();

    /// <summary>Validações registradas — cada uma com método, desfecho e evidência próprios.</summary>
    public ICollection<ActionPlanValidation> Validations { get; set; } = new List<ActionPlanValidation>();

    public bool IsOverdue =>
        Status != ActionPlanStatus.Concluido && DueDate is { } d &&
        d < DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>
    /// [AEGIS-MVP-PRODUCT-03] Uma ação ATIVA ocupa a origem: enquanto ela existe, um novo clique em "criar
    /// plano" abre a existente em vez de duplicar. Concluída (ou legado "vencida"), a origem fica livre — o
    /// problema pode reaparecer e merecer um novo ciclo.
    /// </summary>
    public static bool IsActiveStatus(ActionPlanStatus status) => status
        is ActionPlanStatus.Aberto or ActionPlanStatus.EmAndamento or ActionPlanStatus.AguardandoValidacao;

    public bool IsActive => IsActiveStatus(Status);
}

/// <summary>Per-tenant risk bands and appetite thresholds (configurable).</summary>
public class RiskAppetite : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    /// <summary>
    /// JSON com os cortes máximos de cada banda, lido por RiskScoringService.ParseBands:
    /// <c>{"baixoMax":4,"medioMax":7,"altoMax":9}</c> (score &lt;= baixoMax = Baixo, e assim por diante;
    /// acima de altoMax = Crítico). Chaves ausentes caem no default (4/7/9).
    /// </summary>
    public string ThresholdsJson { get; set; } = "";
}

// ---- Scoring snapshots / derived --------------------------------------------

/// <summary>A computed maturity score at a given granularity, point-in-time.</summary>
public class MaturitySnapshot : Entity
{
    public Guid AssessmentId { get; set; }
    public SnapshotLevel Level { get; set; }
    public string RefCode { get; set; } = "";        // "GV", "GV.OC", "GV.OC-01", or scope id
    public double CurrentScore { get; set; }
    public double TargetScore { get; set; }
    public double Gap { get; set; }
    public DateTimeOffset ComputedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Cyber Risk Criticality Index for a subject (vuln/asset/risk/process), 0–100.</summary>
public class IcrScore : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public IcrSubjectType SubjectType { get; set; }
    public string SubjectRef { get; set; } = "";
    public double Score { get; set; }                // 0..100
    public IcrBand Band { get; set; }
    public string FactorsJson { get; set; } = "";    // breakdown of weighted factors
    public DateTimeOffset ComputedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Configurable ICR weight profile (per tenant or global default).</summary>
public class IcrWeightProfile : Entity
{
    public Guid? TenantId { get; set; }              // null = global default
    public string Name { get; set; } = "default";
    public double TechnicalSeverity { get; set; } = 0.20;
    public double AssetCriticality { get; set; } = 0.20;
    public double BusinessImpact { get; set; } = 0.20;
    public double RecentExploitation { get; set; } = 0.10;
    public double RegulatoryExposure { get; set; } = 0.05;
    public double ControlEffectiveness { get; set; } = 0.15;
    public double OverdueActionPlan { get; set; } = 0.10;
}
