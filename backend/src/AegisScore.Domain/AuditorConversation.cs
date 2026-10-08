namespace AegisScore.Domain;

/// <summary>
/// [AEGIS-AUDITOR-CONTEXT-01] Um turno (pergunta e resposta) de uma conversa com o Auditor Virtual. A conversa pertence ao tenant
/// autenticado E à conta que a iniciou: outra pessoa do mesmo tenant, ou a mesma pessoa noutro tenant, não a lê nem a continua. É relato —
/// nunca evidência de avaliação. Guarda o foco da pergunta (rótulo, sem identificadores) e se a resposta veio do motor simulado.
/// </summary>
public sealed class AuditorConversationTurn : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid AccountId { get; set; }

    /// <summary>1, 2, 3… dentro da conversa — único por conversa: dois turnos concorrentes não se sobrepõem.</summary>
    public int Sequence { get; set; }

    public string Question { get; set; } = "";
    public string Reply { get; set; } = "";

    /// <summary>Foco da tela quando a pergunta foi feita (ex.: "AEGIS NIST · avaliação … · GV.PO-01").</summary>
    public string? FocusLabel { get; set; }

    /// <summary>A resposta veio do motor SIMULADO (demonstração, sem análise por IA).</summary>
    public bool Simulated { get; set; }
}
