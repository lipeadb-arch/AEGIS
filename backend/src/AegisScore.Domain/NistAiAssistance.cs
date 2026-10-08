using System;

namespace AegisScore.Domain;

// ============================================================================
//  [AEGIS-NIST-AI-ASSIST-01] Assistência contextual de IA na jornada do AEGIS NIST
// ============================================================================
// A IA ajuda a INTERPRETAR e REDIGIR (subcategoria, achado e tratamento, resumo executivo da rodada); não decide
// conformidade, não recalcula médias nem altera severidade, revisão ou conclusão. Três registros sustentam isso:
//   • a GERAÇÃO (sugestão identificada, amarrada à impressão digital do contexto usado) — nunca é avaliação;
//   • a INCORPORAÇÃO (append-only): quem levou qual campo de qual geração para um registro, editado ou não;
//   • o RESUMO EXECUTIVO aceito pela pessoa para a rodada e escopo — só ele entra na fotografia publicada.

/// <summary>Sobre o que a assistência foi pedida.</summary>
public enum NistAssistKind
{
    /// <summary>Uma subcategoria da rodada: resultado esperado, evidências, lacunas, perguntas, procedimentos, melhorias.</summary>
    Subcategory = 0,

    /// <summary>Um achado registrado: explicação, impactos, verificações e proposta de tratamento.</summary>
    Finding = 1,

    /// <summary>A rodada e o escopo: interpretação executiva dos resultados determinísticos.</summary>
    ExecutiveSummary = 2,
}

/// <summary>Motor que produziu a geração — dito pelo próprio motor, nunca deduzido depois.</summary>
public enum NistAssistEngineMode
{
    /// <summary>Motor simulado (demonstração): regras fixas, sem provedor e sem análise real de documentos.</summary>
    Simulated = 0,

    /// <summary>Provedor externo autorizado para o tenant (gate de configuração + allowlist).</summary>
    Real = 1,
}

/// <summary>Registro que recebeu conteúdo incorporado de uma geração.</summary>
public enum NistAssistTarget
{
    Evaluation = 0,
    Finding = 1,
    Plan = 2,
    Procedure = 3,
    ExecutiveSummary = 4,
}

/// <summary>
/// Uma GERAÇÃO da assistência: o conteúdo já validado no servidor (citações conferidas contra as fontes do contexto,
/// nível descartado sem base, links não autorizados removidos), as fontes usadas e a impressão digital do contexto. Não
/// guarda prompt, resposta bruta do provedor, segredo nem nome de pessoa do contexto. É SUGESTÃO: nada aqui conta como
/// avaliação, revisão, aprovação, execução ou conclusão.
/// </summary>
public class NistAiAssistance : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid AssessmentId { get; set; }
    public Guid CycleId { get; set; }
    public Guid AssessmentScopeId { get; set; }

    /// <summary>Subcategoria (assistência de subcategoria ou de achado).</summary>
    public string? SubcategoryCode { get; set; }

    /// <summary>Achado (só na assistência de achado).</summary>
    public Guid? FindingId { get; set; }

    public NistAssistKind Kind { get; set; }

    /// <summary>Finalidade pedida no achado: Explain (explicar) ou Treatment (sugerir tratamento).</summary>
    public string? Focus { get; set; }

    /// <summary>SHA-256 do contexto usado (conteúdo + versões dos registros). Mudou o contexto, a sugestão está desatualizada.</summary>
    public string ContextFingerprint { get; set; } = "";

    /// <summary>Resumo legível da base (ex.: "avaliação v3 · 2 evidências · 1 procedimento realizado").</summary>
    public string ContextSummary { get; set; } = "";

    /// <summary>Fontes do contexto (chave, tipo, identificador, rótulo, classificação) — o que uma citação pode apontar.</summary>
    public string SourcesJson { get; set; } = "[]";

    /// <summary>Seções, nível sugerido e procedimentos JÁ validados.</summary>
    public string OutputJson { get; set; } = "{}";

    /// <summary>Texto que cada campo receberia ao ser aplicado ao rascunho (base para saber se a pessoa editou).</summary>
    public string? ApplicableJson { get; set; }

    /// <summary>O que a validação do servidor descartou ou ressalvou.</summary>
    public string? ValidationNotesJson { get; set; }

    public NistAssistEngineMode Mode { get; set; }

    /// <summary>Estado efetivo da IA na geração (Real, Simulated, ProviderNotConfigured, ExternalBlockedForTenant).</summary>
    public string Availability { get; set; } = "";

    public string MethodologyVersion { get; set; } = "";

    /// <summary>O contexto mudou enquanto a geração estava em curso: já nasceu desatualizada.</summary>
    public bool StaleOnArrival { get; set; }

    public Guid? RequestedByAccountId { get; set; }
    public string? RequestedByName { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
}

/// <summary>
/// Uma INCORPORAÇÃO (append-only): a pessoa levou campos de uma geração para um registro pelo fluxo normal de gravação.
/// Guarda o hash do texto sugerido e do gravado de cada campo — a leitura sabe se a pessoa editou e se o texto atual ainda
/// é o incorporado. Não confirma avaliação, não aprova revisão, não executa nem conclui plano.
/// </summary>
public class NistAiIncorporation : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid AssessmentId { get; set; }
    public Guid CycleId { get; set; }
    public Guid AssessmentScopeId { get; set; }
    public string? SubcategoryCode { get; set; }

    public Guid AssistanceId { get; set; }
    public NistAssistTarget TargetKind { get; set; }
    public Guid TargetId { get; set; }

    /// <summary>Lista JSON de { field, label, suggestedHash, incorporatedHash, edited }.</summary>
    public string FieldsJson { get; set; } = "[]";

    /// <summary>A sugestão estava desatualizada e a pessoa declarou tê-la revisado diante do contexto atual.</summary>
    public bool StaleAcknowledged { get; set; }

    public Guid? IncorporatedByAccountId { get; set; }
    public string? IncorporatedByName { get; set; }
    public DateTimeOffset IncorporatedAt { get; set; }
}

/// <summary>
/// O RESUMO EXECUTIVO aceito para uma rodada e escopo (assistido por IA ou redigido pela pessoa). Entra na fotografia
/// publicada só enquanto a base em que foi preparado (<see cref="BasisFingerprint"/>) for a do relatório; a revisão humana
/// posterior é outro ato, de outra pessoa, e vale só para o conteúdo revisado.
/// </summary>
public class NistExecutiveSummary : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid AssessmentId { get; set; }
    public Guid CycleId { get; set; }
    public Guid AssessmentScopeId { get; set; }

    /// <summary>Lista JSON de { key, title, text } — o texto aceito (já com as edições da pessoa).</summary>
    public string SectionsJson { get; set; } = "[]";

    /// <summary>Geração de origem (nula quando redigido pela pessoa).</summary>
    public Guid? AssistanceId { get; set; }

    /// <summary>Impressão digital do relatório (sem interpretação e sem publicação) sobre o qual o resumo foi preparado.</summary>
    public string BasisFingerprint { get; set; } = "";

    public bool Edited { get; set; }
    public bool StaleAcknowledged { get; set; }

    public Guid? AcceptedByAccountId { get; set; }
    public string? AcceptedByName { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }

    /// <summary>SHA-256 das seções aceitas — base da validade da revisão.</summary>
    public string ContentHash { get; set; } = "";

    public Guid? ReviewedByAccountId { get; set; }
    public string? ReviewedByName { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewedContentHash { get; set; }
    public string? ReviewNote { get; set; }

    public int Version { get; set; }
}
