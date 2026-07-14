using System;

namespace AegisScore.Domain;

/// <summary>
/// Estado de conformidade de UMA subcategoria NIST para UM tenant — o núcleo do Aegis Score.
///
/// Diferente de <see cref="SubcategoryEvaluation"/> (maturidade CMMI 1–5, avaliada por *scope*
/// Processo × BU dentro de uma campanha de assessment), este é um registro ÚNICO por
/// tenant × subcategoria: o "estado atual do controle", desacoplado de campanha.
///
/// O score do tenant é um Group By de soma sobre o catálogo global:
///   SUM(CurrentScore) / SUM(NistSubcategory.MaxScorePoints), agregável por Função/Categoria.
/// </summary>
public class TenantControlState : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    // ---- Elo com o catálogo global (imutável) ----
    public Guid SubcategoryId { get; set; }
    public NistSubcategory? Subcategory { get; set; }

    // ---- Estado do controle ----
    /// <summary>Default seguro: assume não-conforme até haver evidência (secure-by-design).</summary>
    public ControlStatus Status { get; set; } = ControlStatus.NonCompliant;

    /// <summary>Pontos obtidos (0..NistSubcategory.MaxScorePoints) — numerador do Aegis Score.</summary>
    public int CurrentScore { get; set; }

    /// <summary>
    /// Procedência do veredito que gravou o estado VIGENTE — define a precedência de escrita: um veredito
    /// <see cref="VerdictSource.Documentary"/> jamais sobrescreve um estado gravado por
    /// <see cref="VerdictSource.Telemetry"/>, nem para cima. A telemetria é a verdade absoluta sobre a
    /// implementação efetiva; um PDF de política não pode maquiar um controle comprovadamente falho.
    ///
    /// Default seguro <c>Documentary</c> (fonte fraca), espelhando o default <c>NonCompliant</c> de
    /// <see cref="Status"/>: estado de procedência desconhecida nunca bloqueia uma correção da telemetria.
    /// </summary>
    public VerdictSource LastVerdictSource { get; set; } = VerdictSource.Documentary;

    public DateTimeOffset LastEvaluatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Justificativa/evidência produzida pela IA (nulo até o motor avaliar).</summary>
    public string? AiEvidence { get; set; }

    /// <summary>
    /// Checklist técnico que justifica o veredito, serializado como JSON (lista de <c>ComplianceCheck</c>).
    /// String simples de propósito — o núcleo trata os checks como um blob explicável de leitura, sem
    /// modelá-los como entidades relacionais (não há consulta por check). Nulo até o motor decompor o veredito.
    /// </summary>
    public string? ChecksJson { get; set; }
}

/// <summary>
/// Foto agregada DIÁRIA da postura de segurança de UM tenant — a inteligência temporal do Aegis
/// Score, que alimenta o gráfico de tendência (modelo Microsoft Secure Score).
///
/// Estratégia de <b>Snapshot Agregado Diário</b>: uma única linha por tenant × dia, já com o
/// numerador (SUM <see cref="TenantControlState.CurrentScore"/>) e o denominador
/// (SUM <see cref="NistSubcategory.MaxScorePoints"/>) consolidados. Evita reexecutar o "Group By
/// de soma" histórico a cada leitura e mantém o overhead de armazenamento no PostgreSQL baixo.
///
/// O percentual exibido é DERIVADO na leitura (TotalAchievedScore / TotalMaxScore) e nunca
/// persistido. Herda de <see cref="Entity"/>, então <c>CreatedAt</c> registra o instante real da
/// geração (quando o worker rodou), complementando o dia lógico de <see cref="SnapshotDate"/>.
/// </summary>
public class TenantScoreSnapshot : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Dia lógico da foto (sem hora, sem fuso). Chave de idempotência junto com TenantId.</summary>
    public DateOnly SnapshotDate { get; set; }

    /// <summary>Numerador consolidado do dia: SUM(TenantControlState.CurrentScore).</summary>
    public int TotalAchievedScore { get; set; }

    /// <summary>Denominador consolidado do dia: SUM(NistSubcategory.MaxScorePoints) do catálogo vigente.</summary>
    public int TotalMaxScore { get; set; }
}
