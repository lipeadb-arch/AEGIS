using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Remediation;

namespace AegisScore.Application.Nist;

// [AEGIS-NIST-JOURNEY-02] Publicação da fotografia de MATURIDADE (avaliação · rodada · escopo) sobre a infraestrutura de
// fotografias existente (PostureSnapshot, tipo NistMaturity, hash e exportação), e importação CSV de trabalho.

/// <summary>
/// Prévia do que será publicado. A <paramref name="ContentFingerprint"/> identifica EXATAMENTE este conteúdo: a publicação
/// a confere, e se o conteúdo mudou desde a revisão na tela ela é recusada — nunca troca a seleção em silêncio.
/// </summary>
public sealed record NistPublicationPreview(
    Guid AssessmentId,
    Guid CycleId,
    Guid ScopeId,
    string ContentFingerprint,
    NistReportSummary Summary,
    IReadOnlyList<NistReportProfile> Functions,
    int Findings,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<string> Warnings);

public sealed record PublishNistCommand(string ExpectedFingerprint);

public sealed record NistPublicationView(
    Guid SnapshotId,
    Guid AssessmentId,
    Guid CycleId,
    string CycleName,
    Guid ScopeId,
    string ScopeName,
    DateTimeOffset CapturedAt,
    string ContentHash,
    string ContentFingerprint,
    double? Current,
    double? Target,
    double? Gap,
    double Coverage,
    int Evaluated,
    int NotApplicable,
    int Subcategories,
    string? PublishedByName,
    string MethodologyVersion);

public interface INistPublicationService
{
    /// <summary>Relatório vivo da rodada (o mesmo documento que seria congelado).</summary>
    Task<NistMaturityReport> BuildReportAsync(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct = default);
    Task<NistPublicationPreview> PreviewAsync(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct = default);
    Task<NistPublicationView> PublishAsync(Guid assessmentId, Guid cycleId, Guid scopeId, PublishNistCommand command, RemediationActor actor, CancellationToken ct = default);
    Task<IReadOnlyList<NistPublicationView>> ListAsync(Guid assessmentId, Guid? cycleId, Guid? scopeId, CancellationToken ct = default);

    /// <summary>Compara duas rodadas da mesma avaliação no mesmo escopo (estado registrado de cada uma).</summary>
    Task<NistCycleComparison> CompareCyclesAsync(Guid assessmentId, Guid scopeId, Guid baseCycleId, Guid targetCycleId, CancellationToken ct = default);
}

// ---- Importação CSV ------------------------------------------------------------------------------------------------

public static class NistImportActions
{
    public const string Create = "Create";
    public const string Update = "Update";
    public const string Unchanged = "Unchanged";
    public const string Error = "Error";
    public const string Conflict = "Conflict";
}

public sealed record NistImportRowView(
    int Line,
    string? Code,
    string? Title,
    string Action,
    IReadOnlyList<string> Messages,
    IReadOnlyList<NistFieldChange> Changes);

/// <summary>
/// Prévia da importação. Nada foi gravado. <paramref name="Token"/> amarra a prévia ao arquivo E às versões vigentes: se
/// qualquer registro mudar antes de aplicar, a aplicação é recusada inteira (409) e uma nova prévia é necessária.
/// </summary>
public sealed record NistImportPreview(
    string Token,
    string? FileName,
    int Rows,
    int Creates,
    int Updates,
    int Unchanged,
    int Errors,
    int Conflicts,
    bool CanApply,
    IReadOnlyList<NistImportRowView> Items,
    IReadOnlyList<string> FileErrors,
    string UpdateRule);

public sealed record NistImportResult(int Created, int Updated, int Unchanged, IReadOnlyList<NistImportRowView> Items);

public sealed record NistCsvFile(byte[] Content, string FileName);

public interface INistImportService
{
    /// <summary>CSV de trabalho da rodada e escopo (todas as subcategorias, valores vigentes e versão de cada uma).</summary>
    Task<NistCsvFile> TemplateAsync(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct = default);
    Task<NistImportPreview> PreviewAsync(Guid assessmentId, Guid cycleId, Guid scopeId, string csv, string? fileName, CancellationToken ct = default);
    Task<NistImportResult> ApplyAsync(Guid assessmentId, Guid cycleId, Guid scopeId, string csv, string? fileName, string expectedToken, RemediationActor actor, CancellationToken ct = default);
}
