using System;

namespace AegisScore.Domain;

// ============================================================================
//  [AEGIS-KNIGHT-CLOSURE-01] Resultado MANUAL estruturado de um controle de referência
// ============================================================================
// Para os controles de referência que o KNIGHT não avalia de forma automatizada — critério organizacional, ausência
// comprovada de método publicado ou acesso que o conector não tem —, a organização registra o resultado da verificação
// manual: o resultado, a justificativa, o responsável e a evidência (a mesma referência de evidência dos planos de ação ou
// um documento da Central de Governança). O registro é IMUTÁVEL: um novo registro substitui o anterior como vigente, e
// "retirado" encerra o resultado sem apagar o histórico. Um resultado manual NUNCA entra na cobertura automatizada, na
// nota KNIGHT nem na aprovação dos controles avaliados — ele aparece à parte, identificado como atestação.

/// <summary>Resultado declarado na verificação manual.</summary>
public enum KnightManualResult
{
    /// <summary>A verificação manual concluiu que o critério é atendido.</summary>
    Compliant = 0,

    /// <summary>A verificação manual concluiu que o critério NÃO é atendido.</summary>
    NonCompliant = 1,

    /// <summary>O critério não se aplica à organização (com a justificativa).</summary>
    NotApplicable = 2,

    /// <summary>O resultado anterior foi retirado (deixa de valer, o histórico fica).</summary>
    Withdrawn = 3,
}

/// <summary>Um registro de resultado manual — tenant-owned e imutável.</summary>
public class KnightManualAssessment : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Chave do controle no catálogo de referência (ex.: <c>CIS-M365-7.0.0:1.1.2</c>).</summary>
    public string ReferenceKey { get; set; } = "";

    public KnightManualResult Result { get; set; }

    /// <summary>Por que o resultado é este — o que foi verificado e como.</summary>
    public string Justification { get; set; } = "";

    /// <summary>Quem responde pelo resultado (pessoa ou área) — informado, não necessariamente quem registrou.</summary>
    public string ResponsibleName { get; set; } = "";

    /// <summary>Referência da evidência (chamado, documento, registro, endereço interno), sanitizada.</summary>
    public string? EvidenceReference { get; set; }

    /// <summary>Documento da Central de Governança usado como evidência (o vínculo; o nome e o hash ficam congelados abaixo).</summary>
    public Guid? EvidenceDocumentId { get; set; }

    /// <summary>Título do documento de evidência no momento do registro.</summary>
    public string? EvidenceDocumentTitle { get; set; }

    /// <summary>SHA-256 do documento de evidência no momento do registro (integridade).</summary>
    public string? EvidenceDocumentSha256 { get; set; }

    /// <summary>Data até a qual o resultado vale (revisão prevista); depois dela ele aparece como vencido.</summary>
    public DateOnly? ValidUntil { get; set; }

    /// <summary>Disposição da referência no catálogo quando o resultado foi registrado (manual, sem método, outro acesso).</summary>
    public string ReferenceDisposition { get; set; } = "";

    /// <summary>Versão do catálogo KNIGHT quando o resultado foi registrado.</summary>
    public string CatalogVersion { get; set; } = "";

    /// <summary>Conta que registrou, resolvida do token.</summary>
    public Guid? RecordedByAccountId { get; set; }

    /// <summary>Nome de quem registrou, como o token o apresentou.</summary>
    public string RecordedByName { get; set; } = "";

    /// <summary>Instante do registro (relógio do servidor).</summary>
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
}
