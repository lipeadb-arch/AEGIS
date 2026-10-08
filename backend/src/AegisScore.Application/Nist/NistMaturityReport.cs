using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace AegisScore.Application.Nist;

// [AEGIS-NIST-JOURNEY-02] O RELATÓRIO de maturidade NIST de uma avaliação · rodada · escopo — o documento que a
// publicação congela (PostureSnapshot.NistReportJson, sob o hash) e do qual HTML, PDF e CSV derivam EXCLUSIVAMENTE.
// Tudo o que o relatório diz está aqui dentro: reexportar uma fotografia antiga nunca consulta nomes, notas, textos ou
// planos atuais. Os números são os da metodologia autoral do AEGIS (escala 1–5), declarada no próprio documento.

public sealed record NistMaturityReport(
    string Schema,
    NistReportClient Client,
    NistReportAssessment Assessment,
    NistReportCycle Cycle,
    NistReportScope Scope,
    NistReportCatalog Catalog,
    NistReportMethodology Methodology,
    NistReportSummary Summary,
    IReadOnlyList<NistReportProfile> Functions,
    IReadOnlyList<NistReportProfile> Categories,
    IReadOnlyList<NistReportSubcategory> Subcategories,
    IReadOnlyList<NistReportFinding> Findings,
    IReadOnlyList<NistReportGap> PriorityGaps,
    IReadOnlyList<string> Limitations,
    /// <summary>Nulo no conteúdo revisado (a impressão digital é calculada sem ele); preenchido na publicação.</summary>
    NistReportPublication? Publication,
    /// <summary>
    /// [AEGIS-NIST-AI-ASSIST-01] Resumo executivo ACEITO por pessoa e ainda válido para esta base, com a procedência. Ausente
    /// (e omitido do JSON) quando não há: o relatório antigo e o relatório sem interpretação seguem idênticos.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NistReportInterpretation? Interpretation = null)
{
    public const string SchemaV1 = "nist-maturity-report-v1";
}

public sealed record NistReportClient(string? Name);

public sealed record NistReportAssessment(
    Guid Id, string Name, string? Description, string Status, DateOnly? StartDate, DateOnly? EndDate);

public sealed record NistReportCycle(
    Guid Id,
    string Name,
    string PeriodKind,
    string PeriodKindLabel,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string Status,
    string? SeedFromCycleName,
    string SeedMode,
    string SeedModeLabel);

public sealed record NistReportScope(Guid Id, string Name, string? Description);

public sealed record NistReportCatalog(string FrameworkName, int Functions, int Categories, int Subcategories);

public sealed record NistReportLevel(int Level, string Name, string Description);

public sealed record NistReportMethodology(
    string Version,
    IReadOnlyList<NistReportLevel> Scale,
    string Statement,
    string AggregationRule,
    string CoverageRule,
    string InstrumentsNote);

public sealed record NistReportSummary(
    double? Current,
    double? Target,
    double? Gap,
    int Subcategories,
    int Evaluated,
    int NotApplicable,
    int InProgress,
    int PendingConfirmation,
    int NotEvaluated,
    double Coverage,
    int WithCurrent,
    int WithTarget,
    int WithGap,
    int IndeterminateGaps,
    int ReviewApproved,
    int ReviewChangesRequested,
    int ReviewOutdated,
    int EvidenceLinked,
    int ProceduresPlanned,
    int ProceduresPerformed,
    int ProceduresNotPerformed,
    int FindingsOpen,
    int FindingsRiskAccepted,
    int FindingsClosed,
    int PlansActive,
    int PlansCompleted,
    int PlansOverdue,
    DateTimeOffset? LastReviewedAt);

public sealed record NistReportProfile(
    string Code,
    string Name,
    string? FunctionCode,
    double? Current,
    double? Target,
    double? Gap,
    int Subcategories,
    int WithCurrent,
    int WithTarget,
    int WithGap,
    int NotApplicable,
    int Evaluated,
    int InProgress,
    int PendingConfirmation,
    int NotEvaluated);

public sealed record NistReportPerson(string? Name, string Kind, string? Contact);

public sealed record NistReportEvidence(
    Guid Id,
    string OriginKind,
    string OriginKindLabel,
    string Type,
    string Title,
    string? Notes,
    string? Uri,
    string? OriginRef,
    string? OriginLabel,
    string? OriginScope,
    DateTimeOffset CollectedAt,
    DateTimeOffset LinkedAt,
    string? RecordedByName);

public sealed record NistReportProcedure(
    Guid Id,
    string Method,
    string MethodLabel,
    string Procedure,
    string Status,
    string StatusLabel,
    string? Outcome,
    string? OutcomeLabel,
    string? Observation,
    DateOnly? PerformedOn,
    string? RecordedByName,
    DateTimeOffset? RecordedAt,
    IReadOnlyList<Guid> EvidenceIds,
    string ContentOrigin);

public sealed record NistReportSubcategory(
    string Code,
    string Title,
    string FunctionCode,
    string CategoryCode,
    string OfficialOutcome,
    string State,
    string StateLabel,
    int? CurrentLevel,
    int? TargetLevel,
    int? Gap,
    bool NotApplicable,
    string? Rationale,
    string? CurrentComments,
    string? TargetComments,
    string? Gaps,
    string? RiskImpact,
    string? ImprovementGuidance,
    NistReportPerson Owner,
    string? AssessorName,
    string? ReviewerName,
    string? RecordedByName,
    DateTimeOffset? RecordedAt,
    string ContentOrigin,
    string? OriginNote,
    string ReviewState,
    string ReviewStateLabel,
    string? ReviewDecisionByName,
    DateTimeOffset? ReviewDecisionAt,
    string? ReviewDecisionNote,
    int Version,
    IReadOnlyList<NistReportProcedure> Procedures,
    IReadOnlyList<NistReportEvidence> Evidence,
    IReadOnlyList<Guid> FindingIds,
    /// <summary>[AEGIS-NIST-AI-ASSIST-01] Campos com conteúdo assistido aceito (omitido do JSON quando não há).</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NistReportAssistance? Assistance = null);

public sealed record NistReportPlan(
    Guid Id,
    string Title,
    string? ProposedAction,
    NistReportPerson Responsible,
    string? ResponsibleArea,
    DateOnly? DueDate,
    string Status,
    string StatusLabel,
    bool IsOverdue,
    string NextStep,
    string? ExecutionNotes,
    DateTimeOffset? ExecutedAt,
    string? ValidationMethodLabel,
    string? ValidationOutcomeLabel,
    string? ValidationEvidenceReference,
    string? ValidatedByName,
    DateTimeOffset? ValidatedAt,
    bool? ValidationAppliesToCurrentCycle,
    bool WasReopened,
    DateTimeOffset CycleStartedAt,
    DateTimeOffset CreatedAt);

public sealed record NistReportFinding(
    Guid Id,
    string SubcategoryCode,
    string SubcategoryTitle,
    string Title,
    string Condition,
    string Risk,
    string Impact,
    string Severity,
    string SeverityLabel,
    string SeverityRationale,
    string Priority,
    string PriorityLabel,
    string PriorityRationale,
    string Recommendation,
    string Status,
    string StatusLabel,
    string? StatusNote,
    IReadOnlyList<Guid> EvidenceIds,
    string? CreatedByName,
    DateTimeOffset CreatedAt,
    int? OriginCurrentLevel,
    int? OriginTargetLevel,
    int? OriginGap,
    NistReportPlan? Plan,
    string TreatmentLabel,
    /// <summary>[AEGIS-NIST-AI-ASSIST-01] Recomendação ou ação do plano com conteúdo assistido aceito (omitido quando não há).</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NistReportAssistance? Assistance = null);

public sealed record NistReportGap(
    string Code,
    string Title,
    string FunctionCode,
    int CurrentLevel,
    int TargetLevel,
    int Gap,
    string? RiskImpact,
    int OpenFindings,
    string? HighestSeverityLabel,
    string TreatmentLabel);

public sealed record NistReportPublication(DateTimeOffset PublishedAt, string? PublishedByName, string ContentFingerprint);

/// <summary>[AEGIS-NIST-AI-ASSIST-01] Um campo do registro cujo texto vigente veio de uma sugestão da IA, com a procedência.</summary>
public sealed record NistReportAssistedField(
    string Field, string Label, Guid AssistanceId, string Mode, DateTimeOffset GeneratedAt, string? IncorporatedByName,
    DateTimeOffset IncorporatedAt, bool Edited, bool StaleAcknowledged);

public sealed record NistReportAssistance(IReadOnlyList<NistReportAssistedField> Fields)
{
    /// <summary>Uma linha legível por incorporação: campos, modo do motor, quem incorporou, quando e se editou.</summary>
    public string Describe() => string.Join("; ", Fields
        .GroupBy(f => (f.AssistanceId, f.IncorporatedByName, f.IncorporatedAt, f.Edited, f.StaleAcknowledged, f.Mode, f.GeneratedAt))
        .Select(g => string.Join(", ", g.Select(f => f.Label)) + " — sugestão " + (g.Key.Mode == "Real" ? "da IA" : "SIMULADA (demonstração)") +
                     " de " + Stamp(g.Key.GeneratedAt) + ", incorporada por " + (g.Key.IncorporatedByName ?? "autor não identificado") +
                     " em " + Stamp(g.Key.IncorporatedAt) + (g.Key.Edited ? " (editada pela pessoa)" : " (sem edição)") +
                     (g.Key.StaleAcknowledged ? " — sugestão desatualizada, revisada pela pessoa" : "")));

    private static string Stamp(DateTimeOffset v) =>
        v.ToUniversalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC";
}

/// <summary>[AEGIS-NIST-AI-ASSIST-01] Seção do resumo executivo aceito.</summary>
public sealed record NistReportInterpretationSection(string Key, string Title, string Text);

/// <summary>
/// [AEGIS-NIST-AI-ASSIST-01] Resumo executivo aceito, congelado com a procedência: origem (assistido ou redigido pela pessoa),
/// modo do motor, quem pediu, quem aceitou/editou e a revisão humana posterior (quando houve e ainda vale).
/// </summary>
public sealed record NistReportInterpretation(
    IReadOnlyList<NistReportInterpretationSection> Sections,
    string Origin,
    string? Mode,
    Guid? AssistanceId,
    DateTimeOffset? GeneratedAt,
    string? RequestedByName,
    string? AcceptedByName,
    DateTimeOffset AcceptedAt,
    bool Edited,
    bool StaleAcknowledged,
    string? ReviewedByName,
    DateTimeOffset? ReviewedAt,
    string BasisFingerprint,
    string Notice)
{
    /// <summary>Procedência em pares rótulo/valor — a mesma em HTML, PDF e CSV.</summary>
    public IReadOnlyList<(string Label, string Value)> Provenance() => new List<(string, string)>
    {
        ("Origem", Origin == "Manual" ? "Redigido pela pessoa"
            : Mode == "Real" ? "Assistido por IA (provedor autorizado), aceito por pessoa"
            : "Assistido pelo motor SIMULADO (demonstração, sem análise real), aceito por pessoa"),
        ("Preparado", GeneratedAt is { } g ? Stamp(g) + (RequestedByName is null ? "" : " a pedido de " + RequestedByName) : "—"),
        ("Aceito", Stamp(AcceptedAt) + " por " + (AcceptedByName ?? "autor não identificado")
            + (Origin == "Manual" ? "" : Edited ? " (com edição da pessoa)" : " (sem edição)")
            + (StaleAcknowledged ? " — sugestão desatualizada, revisada pela pessoa" : "")),
        ("Revisão humana posterior", ReviewedByName is null ? "não houve" : ReviewedByName + " em " + Stamp(ReviewedAt)),
        ("Base do resumo", BasisFingerprint),
    };

    public string Describe() => string.Join(" · ", Provenance().Select(p => p.Label + ": " + p.Value));

    private static string Stamp(DateTimeOffset? v) =>
        v is { } x ? x.ToUniversalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC" : "—";
}

/// <summary>Rótulos pt-BR ÚNICOS da jornada NIST — tela, relatório e CSV dizem a mesma coisa.</summary>
public static class NistLabels
{
    public static string PeriodKind(string kind) => kind switch
    {
        "Monthly" => "Mensal",
        "Quarterly" => "Trimestral",
        _ => "Período informado",
    };

    public static string CycleStatus(string status) => status == "Closed" ? "Encerrada" : "Aberta";

    public static string SeedMode(string mode) => mode switch
    {
        "Reference" => "Rodada anterior como referência",
        "Draft" => "Rascunho a partir de outra rodada (sem revisão humana)",
        _ => "Começou vazia",
    };

    public static string Method(string method) => method switch
    {
        "Examine" => "Examinar",
        "Interview" => "Entrevistar",
        "Test" => "Testar",
        _ => method,
    };

    public static string ProcedureStatus(string status) => status switch
    {
        "InProgress" => "Em execução",
        "Performed" => "Realizado",
        "NotPerformed" => "Não realizado",
        _ => "Planejado",
    };

    public static string Outcome(string? outcome) => outcome switch
    {
        "Satisfactory" => "Satisfatório",
        "PartiallySatisfactory" => "Parcialmente satisfatório",
        "Unsatisfactory" => "Insatisfatório",
        "Inconclusive" => "Inconclusivo",
        _ => "—",
    };

    public static string State(string state) => state switch
    {
        NistSubcategoryStates.Evaluated => "Avaliada",
        NistSubcategoryStates.InProgress => "Em andamento",
        NistSubcategoryStates.PendingConfirmation => "Aguardando confirmação humana",
        NistSubcategoryStates.NotApplicable => "Não se aplica",
        _ => "Não avaliada",
    };

    public static string Review(string state) => state switch
    {
        NistReviewStates.Approved => "Revisão aprovada",
        NistReviewStates.ChangesRequested => "Devolvida para ajuste",
        NistReviewStates.Outdated => "Revisão desatualizada (conteúdo mudou)",
        _ => "Sem decisão do revisor",
    };

    public static string FindingStatus(string status) => status switch
    {
        "RiskAccepted" => "Risco aceito",
        "Closed" => "Encerrado",
        _ => "Aberto",
    };

    public static string Severity(string severity) => severity switch
    {
        "Critical" => "Crítica",
        "High" => "Alta",
        "Medium" => "Média",
        "Low" => "Baixa",
        _ => severity,
    };

    public static int SeverityRank(string severity) => severity switch
    {
        "Critical" => 0,
        "High" => 1,
        "Medium" => 2,
        "Low" => 3,
        _ => 9,
    };

    public static string Priority(string priority) => priority switch
    {
        "Urgent" => "Urgente",
        "High" => "Alta",
        "Medium" => "Média",
        "Low" => "Baixa",
        _ => priority,
    };

    public static string ContentOrigin(string origin) => origin switch
    {
        "CarriedForward" => "Herdado de outra rodada",
        "Imported" => "Importado por CSV",
        _ => "Registrado na tela",
    };

    public static string EvidenceOrigin(string kind) => kind switch
    {
        "GovernanceDocument" => "Documento",
        "KnightIndicator" => "AEGIS KNIGHT",
        "AssetInventory" => "Inventário",
        _ => "Registro do analista",
    };

    public static string FunctionName(string code) => code switch
    {
        "GV" => "Govern — Governar",
        "ID" => "Identify — Identificar",
        "PR" => "Protect — Proteger",
        "DE" => "Detect — Detectar",
        "RS" => "Respond — Responder",
        "RC" => "Recover — Recuperar",
        _ => code,
    };

    public static readonly string[] FunctionOrder = { "GV", "ID", "PR", "DE", "RS", "RC" };
}

/// <summary>Textos FIXOS da metodologia, congelados em cada relatório.</summary>
public static class NistMethodologyText
{
    public const string Statement =
        "Escala de maturidade 1–5 AUTORAL do AEGIS (aegis-methodology-v1). Não é exigência oficial do NIST nem corresponde " +
        "aos Implementation Tiers do CSF 2.0 — o NIST CSF 2.0 descreve resultados, não define pontuação.";

    public const string AggregationRule =
        "Atual e alvo: média simples dos níveis CONFIRMADOS por revisão humana nas subcategorias de cada categoria; a função " +
        "é a média das categorias com valor e o geral, a média das funções com valor. Ausência de nível não é zero; " +
        "\"não se aplica\" sai do cálculo; a lacuna média usa só subcategorias com atual e alvo. Conteúdo herdado de outra " +
        "rodada ou importado por CSV não entra até ser confirmado na tela.";

    public const string CoverageRule =
        "Cobertura = (subcategorias avaliadas + não aplicáveis) ÷ subcategorias do catálogo. Mudança de cobertura ou do " +
        "universo aplicável muda a base das médias.";

    public const string InstrumentsNote =
        "Maturidade NIST (1–5, esta avaliação) ≠ postura do ambiente (AEGIS Score, 0–100) ≠ score do AEGIS KNIGHT. Os três " +
        "instrumentos são independentes e nunca são somados.";
}

/// <summary>Serialização CANÔNICA do relatório e a impressão digital do conteúdo revisado.</summary>
public static class NistReportCanonical
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize(NistMaturityReport report) => JsonSerializer.Serialize(report, Json);

    public static NistMaturityReport Deserialize(string json) =>
        JsonSerializer.Deserialize<NistMaturityReport>(json, Json)
        ?? throw new InvalidOperationException("Relatório NIST congelado ilegível.");

    /// <summary>SHA-256 do conteúdo revisado (sem o bloco de publicação) — o que a tela mostra e a publicação confere.</summary>
    public static string Fingerprint(NistMaturityReport report)
    {
        var content = Serialize(report with { Publication = null });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    /// <summary>Instante truncado a microssegundos em UTC (precisão do PostgreSQL): a impressão digital não varia por provedor.</summary>
    public static DateTimeOffset Micro(DateTimeOffset v)
    {
        var utc = v.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    public static DateTimeOffset? Micro(DateTimeOffset? v) => v is { } x ? Micro(x) : null;

    public static string Level(double? v) =>
        v is { } x ? x.ToString("0.0", CultureInfo.GetCultureInfo("pt-BR")) : "—";
}

// ---- Comparação de rodadas ----------------------------------------------------------------------------------------

public sealed record NistFunctionDelta(string Code, string Name, double? BaseCurrent, double? TargetCurrent, double? CurrentDelta,
    double? BaseTarget, double? TargetTarget, int BaseEvaluated, int TargetEvaluated);

public sealed record NistSubcategoryChange(string Code, string Title, string Kind, string Description);

public sealed record NistCycleComparison(
    bool Compatible,
    IReadOnlyList<string> IncompatibilityReasons,
    Guid BaseCycleId,
    string BaseCycleName,
    Guid TargetCycleId,
    string TargetCycleName,
    double? CurrentDelta,
    double? TargetDelta,
    double CoverageDelta,
    int BaseApplicable,
    int TargetApplicable,
    IReadOnlyList<NistFunctionDelta> Functions,
    IReadOnlyList<NistSubcategoryChange> Changes,
    IReadOnlyList<string> Notes);

/// <summary>
/// Compara duas rodadas da MESMA avaliação e escopo, com a mesma metodologia e catálogo. Mudança de cobertura ou de universo
/// é DITA; queda de nota nunca vira, sozinha, "piora da segurança".
/// </summary>
public static class NistCycleComparer
{
    public static NistCycleComparison Compare(NistMaturityReport baseline, NistMaturityReport target)
    {
        var reasons = new List<string>();
        if (baseline.Assessment.Id != target.Assessment.Id) reasons.Add("As rodadas pertencem a avaliações diferentes.");
        if (baseline.Scope.Id != target.Scope.Id) reasons.Add("Os escopos são diferentes — o universo avaliado não é o mesmo.");
        if (!string.Equals(baseline.Methodology.Version, target.Methodology.Version, StringComparison.Ordinal))
            reasons.Add($"Metodologias diferentes ({baseline.Methodology.Version} × {target.Methodology.Version}).");
        if (!string.Equals(baseline.Catalog.FrameworkName, target.Catalog.FrameworkName, StringComparison.Ordinal))
            reasons.Add($"Catálogos diferentes ({baseline.Catalog.FrameworkName} × {target.Catalog.FrameworkName}).");

        var baseApplicable = baseline.Summary.Subcategories - baseline.Summary.NotApplicable;
        var targetApplicable = target.Summary.Subcategories - target.Summary.NotApplicable;
        if (reasons.Count > 0)
            return new NistCycleComparison(false, reasons, baseline.Cycle.Id, baseline.Cycle.Name, target.Cycle.Id, target.Cycle.Name,
                null, null, 0, baseApplicable, targetApplicable, Array.Empty<NistFunctionDelta>(), Array.Empty<NistSubcategoryChange>(),
                Array.Empty<string>());

        double? Delta(double? a, double? b) => a is { } x && b is { } y ? Math.Round(y - x, 2) : null;

        var functions = NistLabels.FunctionOrder.Select(code =>
        {
            var b = baseline.Functions.FirstOrDefault(f => f.Code == code);
            var t = target.Functions.FirstOrDefault(f => f.Code == code);
            return new NistFunctionDelta(code, NistLabels.FunctionName(code), b?.Current, t?.Current, Delta(b?.Current, t?.Current),
                b?.Target, t?.Target, b?.Evaluated ?? 0, t?.Evaluated ?? 0);
        }).ToList();

        var changes = new List<NistSubcategoryChange>();
        var baseSubs = baseline.Subcategories.ToDictionary(s => s.Code, StringComparer.Ordinal);
        foreach (var t in target.Subcategories.OrderBy(s => s.Code, StringComparer.Ordinal))
        {
            if (!baseSubs.TryGetValue(t.Code, out var b)) continue;
            var bEval = b.State == NistSubcategoryStates.Evaluated;
            var tEval = t.State == NistSubcategoryStates.Evaluated;
            if (b.NotApplicable != t.NotApplicable && (b.State == NistSubcategoryStates.NotApplicable || t.State == NistSubcategoryStates.NotApplicable))
                changes.Add(new NistSubcategoryChange(t.Code, t.Title, "ApplicabilityChanged",
                    t.State == NistSubcategoryStates.NotApplicable ? "Passou a não se aplicar (sai do universo)." : "Voltou a se aplicar (entra no universo)."));
            else if (!bEval && tEval)
                changes.Add(new NistSubcategoryChange(t.Code, t.Title, "NowEvaluated", $"Passou a ser avaliada (atual {t.CurrentLevel})."));
            else if (bEval && !tEval)
                changes.Add(new NistSubcategoryChange(t.Code, t.Title, "NoLongerEvaluated",
                    $"Deixou de ter avaliação confirmada nesta rodada ({NistLabels.State(t.State).ToLowerInvariant()})."));
            else if (bEval && tEval && b.CurrentLevel != t.CurrentLevel)
                changes.Add(new NistSubcategoryChange(t.Code, t.Title, t.CurrentLevel > b.CurrentLevel ? "LevelUp" : "LevelDown",
                    $"Atual {b.CurrentLevel} → {t.CurrentLevel}."));
            else if (bEval && tEval && b.TargetLevel != t.TargetLevel)
                changes.Add(new NistSubcategoryChange(t.Code, t.Title, "TargetChanged",
                    $"Alvo {b.TargetLevel?.ToString(CultureInfo.InvariantCulture) ?? "—"} → {t.TargetLevel?.ToString(CultureInfo.InvariantCulture) ?? "—"}."));
        }

        var notes = new List<string>();
        var coverageDelta = Math.Round(target.Summary.Coverage - baseline.Summary.Coverage, 1);
        if (Math.Abs(coverageDelta) > 0.05)
            notes.Add($"A cobertura mudou de {baseline.Summary.Coverage:0.#}% para {target.Summary.Coverage:0.#}%: as médias foram calculadas sobre conjuntos diferentes de subcategorias.");
        if (baseApplicable != targetApplicable)
            notes.Add($"O universo aplicável mudou de {baseApplicable} para {targetApplicable} subcategorias (\"não se aplica\" diferente entre as rodadas).");
        var current = Delta(baseline.Summary.Current, target.Summary.Current);
        if (current is < 0)
            notes.Add("A média atual caiu. Isso não indica, sozinho, piora da segurança: pode refletir avaliação mais rigorosa, evidência nova, mudança de cobertura ou de universo. Veja as subcategorias que mudaram.");
        if (target.Summary.PendingConfirmation > 0)
            notes.Add($"{target.Summary.PendingConfirmation} subcategoria(s) da rodada {target.Cycle.Name} aguardam confirmação humana e não entram nas médias.");

        return new NistCycleComparison(true, Array.Empty<string>(), baseline.Cycle.Id, baseline.Cycle.Name, target.Cycle.Id, target.Cycle.Name,
            current, Delta(baseline.Summary.Target, target.Summary.Target), coverageDelta, baseApplicable, targetApplicable,
            functions, changes, notes);
    }
}
