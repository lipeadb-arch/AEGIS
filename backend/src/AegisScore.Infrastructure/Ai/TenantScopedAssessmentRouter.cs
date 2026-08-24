using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// Roteador tenant-scoped do <see cref="IAiAssessmentService"/>: por chamada, decide o motor REAL (Anthropic
/// via <see cref="AegisAssessmentService"/>) × SIMULADO (<see cref="StubAssessmentService"/>) pelo gate do Free
/// Tier. Tenant fora da allowlist → NUNCA chama o motor externo. É a ÚNICA ligação de
/// <see cref="IAiAssessmentService"/> na DI: todo consumidor (worker documental, Auditor, entrevistas,
/// assessments, advisories) passa por aqui — nenhum injeta o serviço real diretamente, ignorando o gate.
/// </summary>
public sealed class TenantScopedAssessmentRouter : IAiAssessmentService
{
    private readonly AegisAssessmentService _real;
    private readonly StubAssessmentService _stub;
    private readonly IAiFreeTierGate _gate;
    private readonly IAiTenantResolver _resolver;

    public TenantScopedAssessmentRouter(
        AegisAssessmentService real, StubAssessmentService stub, IAiFreeTierGate gate, IAiTenantResolver resolver)
    {
        _real = real;
        _stub = stub;
        _gate = gate;
        _resolver = resolver;
    }

    /// <summary>REAL só quando o provedor está apto E o tenant vigente está na allowlist; senão, SIMULADO.</summary>
    private async ValueTask<IAiAssessmentService> PickAsync(CancellationToken ct)
    {
        if (!_gate.ProviderConfigured) return _stub;   // atalho: sem provedor, nem resolve slug/banco
        var slug = await _resolver.GetCurrentSlugAsync(ct);
        return _gate.IsExternalAllowedForSlug(slug) ? _real : _stub;
    }

    public async Task<DocumentAnalysis> AnalyzeDocumentAsync(DocumentAnalysisRequest request, CancellationToken ct)
        => await (await PickAsync(ct)).AnalyzeDocumentAsync(request, ct);

    public async Task<DocumentControlVerdict> EvaluateDocumentControlAsync(
        DocumentControlEvaluationRequest request, CancellationToken ct)
        => await (await PickAsync(ct)).EvaluateDocumentControlAsync(request, ct);

    public async Task<MaturitySuggestion> SuggestMaturityAsync(MaturitySuggestionRequest request, CancellationToken ct)
        => await (await PickAsync(ct)).SuggestMaturityAsync(request, ct);

    public async Task<InterviewTurn> ConductInterviewTurnAsync(InterviewContext context, CancellationToken ct)
        => await (await PickAsync(ct)).ConductInterviewTurnAsync(context, ct);

    public async Task<IReadOnlyList<ActionPlanSuggestion>> GenerateActionPlanAsync(ActionPlanRequest request, CancellationToken ct)
        => await (await PickAsync(ct)).GenerateActionPlanAsync(request, ct);

    public async Task<string> GenerateExecutiveReportAsync(ExecutiveReportRequest request, CancellationToken ct)
        => await (await PickAsync(ct)).GenerateExecutiveReportAsync(request, ct);

    public async Task<IReadOnlyList<NormalizedSignal>> NormalizeSignalsAsync(RawSignalBatch batch, CancellationToken ct)
        => await (await PickAsync(ct)).NormalizeSignalsAsync(batch, ct);

    public async Task<AuditorReply> ChatAsync(AuditorChatRequest request, CancellationToken ct)
        => await (await PickAsync(ct)).ChatAsync(request, ct);

    public async Task<AdvisoryDraft> GenerateAdvisoryAsync(AdvisoryGenerationRequest request, CancellationToken ct)
        => await (await PickAsync(ct)).GenerateAdvisoryAsync(request, ct);
}
