using System.Collections.Generic;

namespace AegisScore.Domain;

/// <summary>A versioned control framework. Today: "NIST CSF 2.0". Reference data, shared across tenants.</summary>
public class FrameworkVersion : Entity
{
    public string Name { get; set; } = "";           // "NIST CSF 2.0"
    public string? Source { get; set; }              // "NIST CSWP 29 (2024-02-26)"
    public bool IsActive { get; set; }

    public ICollection<NistFunction> Functions { get; set; } = new List<NistFunction>();
    public ICollection<MaturityLevel> MaturityLevels { get; set; } = new List<MaturityLevel>();
}

/// <summary>CSF Function — GOVERN, IDENTIFY, PROTECT, DETECT, RESPOND, RECOVER.</summary>
public class NistFunction : Entity
{
    public Guid FrameworkVersionId { get; set; }
    public string Code { get; set; } = "";           // "GV"
    public string Name { get; set; } = "";           // "GOVERN (GV)"
    public string Definition { get; set; } = "";
    public int Order { get; set; }

    public ICollection<NistCategory> Categories { get; set; } = new List<NistCategory>();
}

/// <summary>CSF Category — e.g. Organizational Context (GV.OC).</summary>
public class NistCategory : Entity
{
    public Guid FunctionId { get; set; }
    public NistFunction? Function { get; set; }
    public string Code { get; set; } = "";           // "GV.OC"
    public string Name { get; set; } = "";
    public string Definition { get; set; } = "";

    public ICollection<NistSubcategory> Subcategories { get; set; } = new List<NistSubcategory>();
}

/// <summary>CSF Subcategory — the assessable outcome, e.g. GV.OC-01.</summary>
public class NistSubcategory : Entity
{
    public Guid CategoryId { get; set; }
    public NistCategory? Category { get; set; }
    public string Code { get; set; } = "";           // "GV.OC-01"
    public string Description { get; set; } = "";
    public string? ImplementationExamples { get; set; }

    /// <summary>
    /// Peso máximo (denominador) desta subcategoria no Aegis Score. O cálculo é um Group By de soma:
    /// SUM(TenantControlState.CurrentScore) / SUM(MaxScorePoints). Populado pelo seeder do catálogo.
    /// </summary>
    public int MaxScorePoints { get; set; }

    /// <summary>List of mapped controls (CCM, CRI, CIS, SP 800-53). Stored as jsonb.</summary>
    public List<string> InformativeReferences { get; set; } = new();
}

/// <summary>
/// Regra técnica de avaliação de UMA subcategoria do CSF 2.0, extraída do NIST SP 800-53 Rev 5.2.0.
/// Reference data GLOBAL — é o motor do Aegis, compartilhado entre todos os tenants; por isso NÃO
/// implementa <see cref="ITenantOwned"/> (não é carimbada nem filtrada por tenant).
///
/// Modelada LLM-friendly (RAG): os arrays ficam em <c>jsonb</c> na própria linha, sem tabelas 1-N que
/// poluiriam o schema e fragmentariam a consulta da IA. A ligação com o framework é transitiva via
/// <see cref="SubcategoryId"/> → <see cref="NistSubcategory"/>, sem FrameworkVersionId redundante.
/// </summary>
public class AegisAssessmentRule : Entity
{
    /// <summary>FK RÍGIDA ao catálogo: <see cref="NistSubcategory"/>.Id (resolvido do código no seed).</summary>
    public Guid SubcategoryId { get; set; }
    public NistSubcategory? Subcategory { get; set; }

    /// <summary>Código natural da subcategoria (ex.: "DE.CM-01") — legível para RAG e chave do JSON de origem.</summary>
    public string SubcategoryCode { get; set; } = "";

    /// <summary>O que medir tecnicamente (strings curtas). Array → <c>jsonb</c>.</summary>
    public List<string> EvaluationMetrics { get; set; } = new();

    /// <summary>
    /// Lógica técnica/matemática que resolve o status do controle (retorna, no texto, estritamente
    /// Compliant / NonCompliant / MitigatedByThirdParty). É uma string única no JSON — coluna de texto.
    /// </summary>
    public string CalculationLogic { get; set; } = "";

    /// <summary>Fontes de telemetria do ecossistema, ou <c>["MANUAL_AUDIT_REQUIRED"]</c>. Array → <c>jsonb</c>.</summary>
    public List<string> EvidenceRequirements { get; set; } = new();
}

/// <summary>CMMI-style maturity level (1–5) used to score every subcategory.</summary>
public class MaturityLevel : Entity
{
    public Guid FrameworkVersionId { get; set; }
    public int Level { get; set; }                   // 1..5
    public string Name { get; set; } = "";           // "Documented"
    public string Description { get; set; } = "";
    public int Score { get; set; }                   // == Level, kept explicit per the workbook
}
