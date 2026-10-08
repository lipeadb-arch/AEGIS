using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Remediation;

namespace AegisScore.Application.Nist;

// [AEGIS-NIST-AI-ASSIST-01] Assistência contextual de IA na jornada do AEGIS NIST — subcategoria, achado e tratamento, resumo
// executivo da rodada. O contexto é montado NO SERVIDOR (tenant, avaliação, rodada, escopo, subcategoria ou achado, versões e
// impressão digital); a resposta é validada no servidor (citações só de fontes do contexto, nível só com base suficiente,
// links não autorizados removidos) e devolvida como SUGESTÃO identificada. Incorporar exige a ação explícita da pessoa
// pelos fluxos normais de gravação, versão e trilha. A IA interpreta e redige; não decide conformidade, não recalcula
// médias, não altera severidade, revisão ou conclusão.

/// <summary>Classificação de uma fonte (e, por derivação, do que se afirma com ela).</summary>
public static class NistAssistBasis
{
    /// <summary>Fato sustentado pela plataforma: resultado normalizado do KNIGHT, trecho literal validado de documento, inventário.</summary>
    public const string Fact = "Fact";

    /// <summary>Relato do analista: textos da avaliação, observações de procedimentos, notas de evidência, achados, planos.</summary>
    public const string AnalystReport = "AnalystReport";

    /// <summary>Conteúdo herdado de outra rodada, importado ou gerado em análise anterior — ainda não confirmado.</summary>
    public const string Unconfirmed = "Unconfirmed";

    /// <summary>Referência geral (catálogo NIST, explicação do AEGIS, metodologia, indicadores determinísticos).</summary>
    public const string Reference = "Reference";

    /// <summary>Documento só cadastrado: sem trecho ou análise utilizável — não examinado pela assistência.</summary>
    public const string NotExamined = "NotExamined";

    /// <summary>Orientação geral (sem fonte do ambiente): perguntas, procedimentos, recomendações, critérios.</summary>
    public const string General = "General";

    public static string Label(string basis) => basis switch
    {
        Fact => "Fato sustentado",
        AnalystReport => "Relato do analista",
        Unconfirmed => "Não confirmado (herdado, importado ou análise sem trecho)",
        Reference => "Referência (catálogo e metodologia)",
        NotExamined => "Documento não examinado",
        _ => "Orientação geral",
    };

    /// <summary>Força da base para derivar a classificação de uma afirmação (a mais fraca das fontes citadas vence).</summary>
    public static int Strength(string basis) => basis switch
    {
        Fact => 3,
        AnalystReport => 2,
        Unconfirmed => 1,
        _ => 0,
    };
}

// ---- Contexto enviado ao motor (dados, nunca instruções) ------------------------------------------------------------

/// <summary>
/// O CONTEXTO autorizado e pertinente de uma assistência, já minimizado (sem nome de pessoa, contato, segredo ou documento
/// inteiro). O motor real o recebe num bloco de dados não confiáveis; o simulado o lê para montar a demonstração.
/// </summary>
public sealed record NistAssistPrompt(
    string Kind,
    string? Focus,
    NistAssistPromptTarget Target,
    IReadOnlyList<NistAssistPromptSource> Sources,
    IReadOnlyList<NistAssistPromptMetric> Metrics,
    IReadOnlyList<NistAssistPromptPriority> Priorities,
    NistAssistPromptMethodology Methodology,
    IReadOnlyList<string> SectionKeys);

public sealed record NistAssistPromptTarget(
    string AssessmentName,
    string CycleName,
    string CyclePeriod,
    string ScopeName,
    string? ScopeDescription,
    string? SubcategoryCode,
    string? SubcategoryTitle,
    string? EvaluationState,
    int? CurrentLevel,
    int? TargetLevel,
    bool NotApplicable,
    bool HumanConfirmed,
    string? FindingTitle);

/// <summary>Uma fonte citável: a chave (S1, S2…) é a ÚNICA forma de a resposta apontar para ela.</summary>
public sealed record NistAssistPromptSource(
    string Key,
    string Kind,
    string Basis,
    string Title,
    string? Content,
    string? Date,
    string? Status,
    bool IsDemo,
    bool ContentExamined,
    string? Limitation);

/// <summary>Indicador DETERMINÍSTICO do AEGIS (M1, M2…): a IA o explica, não o calcula.</summary>
public sealed record NistAssistPromptMetric(string Key, string Label, string Value);

/// <summary>Item da ordem de prioridade do AEGIS (P1, P2…), já ordenado pelo critério determinístico.</summary>
public sealed record NistAssistPromptPriority(string Key, string Text, IReadOnlyList<string> Sources);

public sealed record NistAssistPromptMethodology(
    string Version, IReadOnlyList<string> Levels, string Statement, string InstrumentsNote, string? PriorityCriterion);

// ---- Rascunho bruto do motor (ainda não validado) -------------------------------------------------------------------

public sealed record NistAssistDraftItem(string Text, IReadOnlyList<string> Sources);

/// <summary>Nível como o motor o escreveu (<see cref="RawValue"/> pode ser inválido: "3.5", "0", "alto"…).</summary>
public sealed record NistAssistDraftLevel(string? RawValue, IReadOnlyList<string> Sources, string? Rationale);

public sealed record NistAssistDraftProcedure(string Method, string Text, IReadOnlyList<string> Sources);

/// <summary>
/// A resposta do motor desserializada, SEM validação semântica — essa é do serviço NIST, que conhece as fontes do contexto.
/// <paramref name="Simulated"/> é dito pelo próprio motor que respondeu.
/// </summary>
public sealed record NistAssistDraft(
    IReadOnlyDictionary<string, IReadOnlyList<NistAssistDraftItem>> Sections,
    NistAssistDraftLevel? Level,
    IReadOnlyList<NistAssistDraftProcedure> Procedures,
    bool Simulated);

// ---- Leituras ---------------------------------------------------------------------------------------------------------

/// <summary>Para onde a fonte leva na interface (âncora na tela ou outra página). Nunca um link externo da resposta.</summary>
public sealed record NistAssistSourceLink(string Target, string? Id, string? Code);

public sealed record NistAssistSourceView(
    string Key,
    string Kind,
    string KindLabel,
    string Basis,
    string BasisLabel,
    string Title,
    string? Detail,
    string? Date,
    string? Status,
    bool IsDemo,
    bool ContentExamined,
    string? Limitation,
    NistAssistSourceLink? Link);

public sealed record NistAssistItemView(string Text, IReadOnlyList<string> Sources, string Basis, string BasisLabel);

public sealed record NistAssistSectionView(string Key, string Title, string Hint, IReadOnlyList<NistAssistItemView> Items);

/// <summary>Nível sugerido — só existe quando há base suficiente (fontes confirmadas citadas) e o valor é válido na escala.</summary>
public sealed record NistAssistLevelView(int Level, string LevelName, string Rationale, IReadOnlyList<string> Sources, string MethodologyVersion);

public sealed record NistAssistProcedureView(string Method, string MethodLabel, string Procedure, IReadOnlyList<string> Sources);

/// <summary>Estado da IA para a assistência NIST (o mesmo gate do resto do produto, com a diferença dita).</summary>
public sealed record NistAssistAvailabilityView(string State, string Label, string Detail, bool CanGenerate);

/// <summary>
/// Uma GERAÇÃO, pronta para revisão: seções validadas, fontes citáveis, nível (ou a razão de não haver), procedimentos,
/// textos aplicáveis ao rascunho e notas da validação. <paramref name="Current"/> diz se o contexto ainda é o mesmo.
/// </summary>
public sealed record NistAssistView(
    Guid Id,
    string Kind,
    string? Focus,
    Guid AssessmentId,
    Guid CycleId,
    Guid ScopeId,
    string? SubcategoryCode,
    Guid? FindingId,
    string Mode,
    string Availability,
    string ModeLabel,
    DateTimeOffset GeneratedAt,
    string? RequestedByName,
    string ContextFingerprint,
    string ContextSummary,
    bool Current,
    bool StaleOnArrival,
    bool Reused,
    IReadOnlyList<NistAssistSourceView> Sources,
    IReadOnlyList<NistAssistSectionView> Sections,
    NistAssistLevelView? Level,
    string? LevelNote,
    IReadOnlyList<NistAssistProcedureView> Procedures,
    IReadOnlyDictionary<string, string> Applicable,
    IReadOnlyList<string> ValidationNotes,
    string Disclaimer);

/// <summary>O contexto que a assistência usaria AGORA (sem chamar a IA): conferir fontes e saber se uma sugestão envelheceu.</summary>
public sealed record NistAssistContextView(
    string Fingerprint,
    string Summary,
    IReadOnlyList<NistAssistSourceView> Sources,
    NistAssistAvailabilityView Availability,
    NistAssistView? Latest);

/// <summary>Um campo de um registro cujo texto VIGENTE veio de uma geração (editado ou não pela pessoa).</summary>
public sealed record NistAssistedFieldView(
    string Field,
    string Label,
    Guid AssistanceId,
    string Mode,
    DateTimeOffset GeneratedAt,
    string? RequestedByName,
    string? IncorporatedByName,
    DateTimeOffset IncorporatedAt,
    bool Edited,
    bool StaleAcknowledged);

// ---- Comandos -----------------------------------------------------------------------------------------------------

/// <summary>
/// Pedido de assistência. <paramref name="Reuse"/>: devolve a última geração deste alvo enquanto o contexto for o mesmo (sem
/// nova chamada). <paramref name="Focus"/> (achado): Explain ou Treatment.
/// </summary>
public sealed record NistAssistRequest(bool Reuse = true, string? Focus = null);

/// <summary>
/// Referência à geração cujo conteúdo a pessoa levou ao registro. <paramref name="Fields"/>: campos aplicados.
/// <paramref name="AcknowledgeStale"/>: a sugestão envelheceu e a pessoa declara tê-la revisado diante do contexto atual.
/// </summary>
public sealed record NistAssistanceReference(Guid AssistanceId, IReadOnlyList<string>? Fields, bool AcknowledgeStale = false);

/// <summary>Procedimento sugerido escolhido pela pessoa para entrar como PLANEJADO (planejar não é realizar).</summary>
public sealed record NistAssistedProcedureInput(string Method, string Procedure);

public sealed record PlanNistProceduresFromAssistanceCommand(
    Guid AssistanceId, IReadOnlyList<NistAssistedProcedureInput> Procedures, bool AcknowledgeStale = false);

public sealed record NistExecutiveSectionInput(string Key, string Text);

/// <summary>Aceitar (ou editar) o resumo executivo. Sem <paramref name="AssistanceId"/>, é redigido pela pessoa.</summary>
public sealed record SaveNistExecutiveSummaryCommand(
    IReadOnlyList<NistExecutiveSectionInput> Sections, Guid? AssistanceId, int ExpectedVersion, bool AcknowledgeStale = false);

public sealed record ReviewNistExecutiveSummaryCommand(int ExpectedVersion, string? Note);

public sealed record NistExecutiveSectionView(string Key, string Title, string Text);

public sealed record NistExecutiveSummaryView(
    Guid Id,
    IReadOnlyList<NistExecutiveSectionView> Sections,
    string Origin,
    string? Mode,
    Guid? AssistanceId,
    DateTimeOffset? GeneratedAt,
    string? RequestedByName,
    string? AcceptedByName,
    DateTimeOffset AcceptedAt,
    bool Edited,
    bool StaleAcknowledged,
    bool Current,
    string? ReviewedByName,
    DateTimeOffset? ReviewedAt,
    bool ReviewCurrent,
    string? ReviewNote,
    int Version);

/// <summary>Seleção NIST da tela ativa, para o Auditor Virtual fundamentar a conversa (conferida no servidor).</summary>
public sealed record NistAuditorSelection(Guid AssessmentId, Guid CycleId, Guid ScopeId, string? SubcategoryCode);

/// <summary>Contexto NIST compacto entregue ao Auditor: o mesmo montador da assistência, sem gerar sugestão.</summary>
public sealed record NistAuditorContext(
    string Assessment, string Cycle, string Scope, string? Subcategory, string Summary,
    IReadOnlyList<string> Facts, IReadOnlyList<string> Notes);

public interface INistAssistService
{
    Task<NistAssistAvailabilityView> AvailabilityAsync(CancellationToken ct = default);

    Task<NistAssistContextView> SubcategoryContextAsync(Guid assessmentId, Guid cycleId, Guid scopeId, string code, CancellationToken ct = default);
    Task<NistAssistView> AssistSubcategoryAsync(Guid assessmentId, Guid cycleId, Guid scopeId, string code, NistAssistRequest request, RemediationActor actor, CancellationToken ct = default);

    Task<NistAssistContextView> FindingContextAsync(Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, string? focus, CancellationToken ct = default);
    Task<NistAssistView> AssistFindingAsync(Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, NistAssistRequest request, RemediationActor actor, CancellationToken ct = default);

    Task<NistAssistContextView> ExecutiveContextAsync(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct = default);
    Task<NistAssistView> AssistExecutiveAsync(Guid assessmentId, Guid cycleId, Guid scopeId, NistAssistRequest request, RemediationActor actor, CancellationToken ct = default);

    Task<NistExecutiveSummaryView?> GetExecutiveSummaryAsync(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct = default);
    Task<NistExecutiveSummaryView> SaveExecutiveSummaryAsync(Guid assessmentId, Guid cycleId, Guid scopeId, SaveNistExecutiveSummaryCommand command, RemediationActor actor, CancellationToken ct = default);
    Task<NistExecutiveSummaryView> ReviewExecutiveSummaryAsync(Guid assessmentId, Guid cycleId, Guid scopeId, ReviewNistExecutiveSummaryCommand command, RemediationActor actor, CancellationToken ct = default);
    Task WithdrawExecutiveSummaryAsync(Guid assessmentId, Guid cycleId, Guid scopeId, int expectedVersion, RemediationActor actor, CancellationToken ct = default);

    /// <summary>Contexto NIST para o Auditor Virtual — nulo quando a seleção não existe neste tenant.</summary>
    Task<NistAuditorContext?> AuditorContextAsync(NistAuditorSelection selection, CancellationToken ct = default);
}
