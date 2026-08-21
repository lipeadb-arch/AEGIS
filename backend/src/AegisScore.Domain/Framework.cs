using System.Collections.Generic;

namespace AegisScore.Domain;

/// <summary>
/// Uma versão de framework de controles. Hoje: "NIST CSF 2.0". Reference data, compartilhada entre tenants.
///
/// ⚠️ SEPARAÇÃO DE RESPONSABILIDADES (AEGIS-MVP-POSTURE-01): esta versão AGREGA conteúdo de origens
/// distintas, que NÃO se confundem:
///   • conteúdo OFICIAL do NIST — <see cref="Functions"/> (funções/categorias/subcategorias, exemplos e
///     referências informativas), carregado de <c>nist_csf_2_0_catalog.json</c>;
///   • metodologia AUTORAL do AEGIS — <see cref="MaturityLevels"/> (escala CMMI-like de 5 níveis, que NÃO
///     são os 4 Implementation Tiers oficiais) e os pesos de <see cref="NistSubcategory.MaxScorePoints"/>,
///     carregados de <c>aegis_methodology.json</c>.
/// A proveniência auditável de cada conjunto (oficial × derivado, versão, hash) fica em
/// <see cref="ReferenceDatasetProvenance"/>. O nome oficial nunca declara a escala nem os pesos como NIST.
/// </summary>
public class FrameworkVersion : Entity
{
    public string Name { get; set; } = "";           // "NIST CSF 2.0"
    public string? Source { get; set; }              // "NIST CSWP 29 (2024-02-26)"
    public bool IsActive { get; set; }

    public ICollection<NistFunction> Functions { get; set; } = new List<NistFunction>();
    public ICollection<MaturityLevel> MaturityLevels { get; set; } = new List<MaturityLevel>();

    /// <summary>Metadados de auditoria por conjunto de dados de referência (catálogo, metodologia, regras).</summary>
    public ICollection<ReferenceDatasetProvenance> Provenance { get; set; } = new List<ReferenceDatasetProvenance>();
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
/// Natureza TIPADA da prova que uma regra exige — contrato explícito, não mais inferido do texto de
/// <c>evidence_requirements</c> em cada leitura (AEGIS-MVP-POSTURE-01). Derivada de forma determinística
/// no seed a partir do vocabulário bimodal do catálogo (<c>MANUAL_AUDIT_REQUIRED</c> × "Ferramenta: …")
/// e PERSISTIDA, para que os consumidores leiam o tipo em vez de re-parsear a string.
/// </summary>
public enum RuleEvidenceType
{
    /// <summary>Prova por telemetria (sinal de ferramenta), coletável por conector.</summary>
    Telemetry = 0,

    /// <summary>Prova documental / auditoria manual/atestada — nenhuma ferramenta a coleta sozinha.</summary>
    Documentation = 1,

    /// <summary>A regra exige AS DUAS naturezas de prova (telemetria E documental).</summary>
    Both = 2,
}

/// <summary>
/// Regra técnica de avaliação de UMA subcategoria do CSF 2.0 — rubrica AUTORAL do AEGIS, derivada do NIST
/// SP 800-53 Rev 5 (conforme a proveniência do conjunto). Reference data GLOBAL — é o motor do Aegis,
/// compartilhado entre todos os tenants; por isso NÃO implementa <see cref="ITenantOwned"/> (não é carimbada
/// nem filtrada por tenant). Conjunto DERIVADO (não é conteúdo oficial do NIST); a proveniência fica em
/// <see cref="ReferenceDatasetProvenance"/>.
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
    /// Rubrica CONSULTIVA (texto): descreve como um humano/IA resolveria o status do controle. NÃO é executada
    /// por nenhum motor determinístico neste pacote — é persistida e enviada como TEXTO de apoio para a IA
    /// interpretar, nunca desta fórmula textual. O veredito por TELEMETRIA é determinístico (telemetria +
    /// <c>SignalMapping.ScoringHint</c>); o veredito DOCUMENTAL ainda passa por JULGAMENTO da IA, com validação
    /// de trecho LITERAL e reconciliação posteriores (não é totalmente determinístico). Os prompts que a
    /// enviam para a IA a rotulam como rubrica consultiva (não como fórmula executada) — vide os context builders.
    /// </summary>
    public string CalculationLogic { get; set; } = "";

    /// <summary>Fontes de telemetria do ecossistema, ou <c>["MANUAL_AUDIT_REQUIRED"]</c>. Array → <c>jsonb</c>.</summary>
    public List<string> EvidenceRequirements { get; set; } = new();

    /// <summary>
    /// Natureza TIPADA da evidência (contrato explícito, não inferência de texto). Preenchida no seed a
    /// partir de <see cref="EvidenceRequirements"/>: só <c>MANUAL_AUDIT_REQUIRED</c> → Documentation; só
    /// fontes de ferramenta → Telemetry; ambos → Both.
    /// </summary>
    public RuleEvidenceType EvidenceType { get; set; } = RuleEvidenceType.Telemetry;
}

/// <summary>
/// Nível de maturidade CMMI-like (1–5) — metodologia AUTORAL do AEGIS, carregada de
/// <c>aegis_methodology.json</c>. NÃO corresponde aos 4 Implementation Tiers oficiais do NIST CSF 2.0
/// (Partial/Risk Informed/Repeatable/Adaptive); não deve ser apresentado como conteúdo NIST.
/// </summary>
public class MaturityLevel : Entity
{
    public Guid FrameworkVersionId { get; set; }
    public int Level { get; set; }                   // 1..5
    public string Name { get; set; } = "";           // "Documented"
    public string Description { get; set; } = "";
    public int Score { get; set; }                   // == Level, kept explicit per the workbook
}

/// <summary>Conjunto de dados de referência cuja proveniência é rastreada para auditoria.</summary>
public enum ReferenceDatasetKind
{
    /// <summary>Catálogo oficial NIST CSF 2.0 (<c>nist_csf_2_0_catalog.json</c>).</summary>
    NistCatalog = 0,

    /// <summary>Metodologia autoral AEGIS: maturidade + pesos (<c>aegis_methodology.json</c>).</summary>
    AegisMethodology = 1,

    /// <summary>Rubricas de avaliação AEGIS (<c>aegis_assessment_rules.json</c>).</summary>
    AegisAssessmentRules = 2,
}

/// <summary>Classifica a AUTORIA do conteúdo — o eixo que impede apresentar autoral como oficial.</summary>
public enum DatasetClassification
{
    /// <summary>Conteúdo oficial de terceiro (ex.: NIST), reproduzido sem alteração de mérito.</summary>
    Official = 0,

    /// <summary>Conteúdo derivado/autoral do AEGIS (metodologia, pesos, rubricas).</summary>
    Derived = 1,
}

/// <summary>
/// [AEGIS-MVP-POSTURE-01] Metadados de auditoria de UMA REVISÃO de um conjunto de dados de referência usado
/// pelo sistema (catálogo NIST, metodologia AEGIS, regras). Reference data GLOBAL, associada à
/// <see cref="FrameworkVersion"/>; não é <see cref="ITenantOwned"/>.
///
/// Sustenta a proveniência exigida: identificador, versão/schema, origem, publicação/release, URL oficial
/// (quando comprovável), data de obtenção, hash do conteúdo, indicação clara de oficial × derivado e versão
/// da metodologia AEGIS associada. Nada aqui é inventado: o que não é comprovável a partir do artefato local
/// fica NULO (desconhecido). O <see cref="ContentHash"/> é calculado sobre a forma canônica do conteúdo no
/// seed — é o que torna a atualização determinística (conteúdo idêntico → mesmo hash → sem alteração).
///
/// HISTÓRICO PRESERVADO: cada atualização de conteúdo grava uma NOVA revisão (<see cref="Revision"/>++,
/// <see cref="IsCurrent"/> = true) e apenas MARCA a anterior como superada (<see cref="IsCurrent"/> = false,
/// <see cref="SupersededAt"/>). Um hash antigo nunca desaparece — a rastreabilidade de qual bundle produziu
/// cada snapshot permanece auditável.
/// </summary>
public class ReferenceDatasetProvenance : Entity
{
    public Guid FrameworkVersionId { get; set; }

    public ReferenceDatasetKind Kind { get; set; }

    /// <summary>Revisão do conjunto (1, 2, 3…) — cresce a cada mudança de conteúdo.</summary>
    public int Revision { get; set; } = 1;

    /// <summary>True na revisão vigente; exatamente UMA por (FrameworkVersion, Kind) — invariante de banco.</summary>
    public bool IsCurrent { get; set; } = true;

    /// <summary>Quando esta revisão foi registrada.</summary>
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>Quando esta revisão foi superada por outra; nulo enquanto vigente.</summary>
    public DateTimeOffset? SupersededAt { get; set; }

    /// <summary>Identificador estável do conjunto (ex.: "nist-csf-2.0-catalog").</summary>
    public string Identifier { get; set; } = "";

    public DatasetClassification Classification { get; set; }

    /// <summary>Versão do schema/estrutura do artefato (ex.: "1.0").</summary>
    public string SchemaVersion { get; set; } = "";

    /// <summary>Origem declarada (ex.: "NIST Cybersecurity Framework (CSF) 2.0").</summary>
    public string Origin { get; set; } = "";

    /// <summary>Referência oficial da origem (ex.: "NIST CSWP 29"); nula quando não aplicável.</summary>
    public string? OfficialReference { get; set; }

    /// <summary>Publicação/release da origem (ex.: "2024-02-26"); nulo quando não comprovável.</summary>
    public string? Release { get; set; }

    /// <summary>URL oficial, quando comprovável; nulo quando não registrada no artefato local.</summary>
    public string? OfficialUrl { get; set; }

    /// <summary>Data de obtenção do conjunto; nula quando desconhecida (nunca inventada).</summary>
    public string? ObtainedOn { get; set; }

    /// <summary>Catálogo ao qual a metodologia/regras se aplica (ex.: "nist-csf-2.0-catalog"); nulo no catálogo.</summary>
    public string? AppliesToCatalog { get; set; }

    /// <summary>Hash SHA-256 (hex) do conteúdo canônico — âncora de integridade e de detecção de mudança.</summary>
    public string ContentHash { get; set; } = "";

    /// <summary>Versão da metodologia AEGIS associada (ex.: "aegis-methodology-v1"), quando aplicável.</summary>
    public string? MethodologyVersion { get; set; }

    /// <summary>Nota de auditoria declarada no artefato (contexto de oficial × derivado); preservada, não descartada.</summary>
    public string? Notes { get; set; }
}
