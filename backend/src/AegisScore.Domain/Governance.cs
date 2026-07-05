using System;
using System.Collections.Generic;

namespace AegisScore.Domain;

// =============================================================================
// GOVERN (GV) — Document Hub + Auditor Virtual (GRC)
// A cobertura do NIST é medida por DUAS fontes de evidência:
//   1) Documentos (a teoria): políticas/normas/contratos ingeridos e lidos pela IA.
//   2) Entrevistas (a prática): o chatbot GRC fecha lacunas e gera riscos identificados.
// O elo entre as duas é o ledger SubcategoryCoverage.
// =============================================================================

/// <summary>
/// Um documento de compliance ingerido no hub (upload manual ou integração). Guarda os metadados
/// do arquivo + o resultado da leitura da IA (quais controles NIST o texto atende).
/// </summary>
public class GovernanceDocument : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }   // carimbado no SaveChangesAsync (fail-closed)

    // ---- Metadados do arquivo ----
    public string Title { get; set; } = "";
    public GovernanceDocumentType Type { get; set; } = GovernanceDocumentType.Politica;
    public DocumentSource Source { get; set; } = DocumentSource.UploadManual;
    public string? SourceReference { get; set; }     // URL SharePoint / pageId Confluence / null p/ upload
    public string? FileName { get; set; }
    public string? ContentType { get; set; }         // "application/pdf", "text/plain"...
    public long? FileSizeBytes { get; set; }
    public string? StorageUri { get; set; }          // onde o binário foi guardado (disco/blob)
    public string? Sha256 { get; set; }              // integridade + deduplicação
    public DateOnly? DocumentDate { get; set; }      // data do próprio documento, se conhecida
    public GovernanceStatus Status { get; set; } = GovernanceStatus.Vigente;

    // ---- Pipeline de leitura pela IA ----
    public AiAnalysisStatus AnalysisStatus { get; set; } = AiAnalysisStatus.Pending;
    public DateTimeOffset? AnalysisQueuedAt { get; set; }
    public DateTimeOffset? AnalyzedAt { get; set; }
    public string? AnalysisSummary { get; set; }     // resumo textual produzido pela IA
    public string? AnalysisError { get; set; }       // preenchido quando AnalysisStatus == Failed
    public string? ModelUsed { get; set; }           // ex.: "claude-opus-4-8" (rastreabilidade)

    // ---- Resultado do mapeamento NIST (filho) ----
    public ICollection<DocumentControlMapping> ControlMappings { get; set; } = new List<DocumentControlMapping>();
}

/// <summary>
/// Um controle NIST que a IA identificou como atendido pelo texto do documento.
/// Espelha DocumentClaim(SubcategoryCode, Claim, Confidence) do IAiAssessmentService.
/// </summary>
public class DocumentControlMapping : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }   // denormalizado: defesa em profundidade + stamping automático
    public Guid GovernanceDocumentId { get; set; }

    public string SubcategoryCode { get; set; } = "";   // "GV.PO-01"
    public double Confidence { get; set; }              // 0..1, vindo da IA
    public string? Evidence { get; set; }               // trecho/claim que embasou o mapeamento
    public bool AnalystConfirmed { get; set; }          // human-in-the-loop: analista validou
}

/// <summary>
/// Verdade de cobertura por subcategoria, por tenant. Atualizada pelo pipeline de documentos E
/// pelas respostas de entrevista (governança híbrida). É a fonte do mapa de gaps.
/// </summary>
public class SubcategoryCoverage : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public string SubcategoryCode { get; set; } = "";           // "GV.PO-01"

    public CoverageStatus Status { get; set; } = CoverageStatus.NaoCoberto;
    public CoverageEvidenceSource EvidenceSource { get; set; } = CoverageEvidenceSource.None;

    public Guid? OriginDocumentId { get; set; }                 // documento que cobriu (se houver)
    public Guid? OriginInterviewSessionId { get; set; }         // entrevista que cobriu (se houver)
    public double? Confidence { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? LastEvaluatedAt { get; set; }
}

/// <summary>
/// Uma sessão do Auditor Virtual (chatbot GRC), por tenant. Nasce dos gaps do NIST não cobertos
/// pelos documentos e guarda o histórico de auditoria (perguntas da IA + respostas do usuário).
/// </summary>
public class GrcInterviewSession : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public string Title { get; set; } = "";              // "Diagnóstico de Gaps — GV/PR — jul/2026"
    public Guid? AssessmentId { get; set; }              // opcional: vincula a um diagnóstico
    public GrcInterviewStatus Status { get; set; } = GrcInterviewStatus.Active;

    /// <summary>Gaps-alvo da sessão: subcategorias NaoCoberto/Parcial na abertura. jsonb.</summary>
    public List<string> TargetSubcategoryCodes { get; set; } = new();

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public ICollection<GrcInterviewMessage> Messages { get; set; } = new List<GrcInterviewMessage>();
}

/// <summary>Uma mensagem da conversa de auditoria (pergunta da IA, resposta do usuário, ou sistema).</summary>
public class GrcInterviewMessage : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }   // denormalizado: defesa em profundidade + stamping automático
    public Guid SessionId { get; set; }

    public GrcMessageRole Role { get; set; }
    public string Content { get; set; } = "";
    public int Sequence { get; set; }                    // ordem na conversa (0,1,2,…)
    public string? TargetSubcategoryCode { get; set; }   // subcategoria que a pergunta investiga
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Um risco identificado pelo Auditor Virtual ao confirmar uma lacuna NA PRÁTICA (controle ausente
/// ou parcial). Guarda Descrição, Causa e Consequência; pode ser promovido ao Sistema de Riscos.
/// </summary>
public class IdentifiedRisk : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public string Title { get; set; } = "";
    public string Description { get; set; } = "";        // Descrição
    public string? Cause { get; set; }                   // Causa
    public string? Consequence { get; set; }             // Consequência

    public string SubcategoryCode { get; set; } = "";    // subcategoria NIST relacionada
    public Guid? AssessmentId { get; set; }              // vínculo com o assessment (se houver)
    public Guid? OriginInterviewSessionId { get; set; }  // sessão do chatbot que o gerou

    public bool PromotedToRisk { get; set; }             // já virou Risk formal no registro?
    public Guid? RiskId { get; set; }                    // link p/ o Sistema de Gestão de Riscos

    public DateTimeOffset IdentifiedAt { get; set; } = DateTimeOffset.UtcNow;
}
