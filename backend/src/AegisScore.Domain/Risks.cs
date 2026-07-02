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

    public ICollection<RiskEvaluation> Evaluations { get; set; } = new List<RiskEvaluation>();
    public ICollection<ActionPlan> ActionPlans { get; set; } = new List<ActionPlan>();
}

/// <summary>Inherent or residual evaluation: Probability + Impact + ProcessValue (each 1–4).</summary>
public class RiskEvaluation : Entity
{
    public Guid RiskId { get; set; }
    public RiskPhase Phase { get; set; } = RiskPhase.Inherent;
    public int ProcessValue { get; set; }            // 1..4
    public int Probability { get; set; }             // 1..4
    public int Impact { get; set; }                  // 1..4
    public int RiskScore { get; set; }               // computed (3..12)
    public RiskLevel RiskLevel { get; set; }
    public DateTimeOffset EvaluatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Risk treatment / action plan (Plano de Ação): what/who/how/when + status.</summary>
public class ActionPlan : Entity
{
    public Guid RiskId { get; set; }
    public RiskTreatmentType Treatment { get; set; } = RiskTreatmentType.Mitigar;
    public string? Description { get; set; }
    public string? ResponsibleArea { get; set; }
    public string? ResponsiblePerson { get; set; }
    public string? HowToImplement { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public ActionPlanStatus Status { get; set; } = ActionPlanStatus.Aberto;
    public DateTimeOffset? CompletedAt { get; set; }

    public bool IsOverdue =>
        Status != ActionPlanStatus.Concluido && DueDate is { } d &&
        d < DateOnly.FromDateTime(DateTime.UtcNow);
}

/// <summary>Per-tenant risk bands and appetite thresholds (configurable).</summary>
public class RiskAppetite : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    /// <summary>JSON: band cutoffs and appetite limit, e.g. {"baixo":[3,4],"medio":[5,7],...,"limit":8}.</summary>
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
