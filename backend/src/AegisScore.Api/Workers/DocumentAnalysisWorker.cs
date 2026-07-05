using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Api.Workers;

/// <summary>
/// Consome a fila de leitura: para cada documento, extrai o texto, chama a IA para mapear os
/// controles NIST e atualiza o ledger de cobertura. Roda sob um SystemTenantContext do tenant
/// dono do documento (o stamping é fail-closed e não há header de request aqui).
/// </summary>
public sealed class DocumentAnalysisWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IDocumentAnalysisQueue _queue;
    private readonly ILogger<DocumentAnalysisWorker> _log;

    public DocumentAnalysisWorker(
        IServiceScopeFactory scopes, IDocumentAnalysisQueue queue, ILogger<DocumentAnalysisWorker> log)
    {
        _scopes = scopes;
        _queue = queue;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var docId in _queue.DequeueAllAsync(ct))
        {
            try { await ProcessAsync(docId, ct); }
            catch (Exception ex) { _log.LogError(ex, "Falha inesperada ao analisar documento {DocId}", docId); }
        }
    }

    private async Task ProcessAsync(Guid docId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var options = sp.GetRequiredService<DbContextOptions<AegisScoreDbContext>>();
        var ai = sp.GetRequiredService<IAiAssessmentService>();
        var storage = sp.GetRequiredService<IDocumentStorage>();
        var extractors = sp.GetServices<IDocumentTextExtractor>().ToList();

        // Descobre o tenant dono (sem tenant ambiente → IgnoreQueryFilters).
        Guid tenantId;
        await using (var probe = new AegisScoreDbContext(options, new SystemTenantContext(null)))
        {
            var owner = await probe.GovernanceDocuments.IgnoreQueryFilters()
                .Where(d => d.Id == docId)
                .Select(d => (Guid?)d.TenantId)
                .FirstOrDefaultAsync(ct);
            if (owner is null) return;
            tenantId = owner.Value;
        }

        await using var db = new AegisScoreDbContext(options, new SystemTenantContext(tenantId));
        var doc = await db.GovernanceDocuments.FirstOrDefaultAsync(d => d.Id == docId, ct);
        if (doc is null || doc.StorageUri is null) return;

        doc.AnalysisStatus = AiAnalysisStatus.Processing;
        await db.SaveChangesAsync(ct);

        try
        {
            await using var stream = await storage.OpenAsync(doc.StorageUri, ct);
            var extractor = extractors.FirstOrDefault(e => e.CanHandle(doc.ContentType, doc.FileName))
                ?? throw new NotSupportedException($"Sem extrator de texto para '{doc.ContentType ?? doc.FileName}'.");
            var text = await extractor.ExtractAsync(stream, doc.ContentType, ct);

            var analysis = await ai.AnalyzeDocumentAsync(new DocumentAnalysisRequest(tenantId, text, doc.FileName), ct);

            foreach (var claim in analysis.Claims)
            {
                db.DocumentControlMappings.Add(new DocumentControlMapping
                {
                    GovernanceDocumentId = doc.Id,
                    SubcategoryCode = claim.SubcategoryCode,
                    Confidence = claim.Confidence,
                    Evidence = claim.Claim,
                });
                await UpsertCoverageFromDocumentAsync(db, claim.SubcategoryCode, doc.Id, claim.Confidence, ct);
            }

            doc.AnalysisSummary = analysis.Summary;
            doc.AnalysisStatus = AiAnalysisStatus.Analyzed;
            doc.AnalyzedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            doc.AnalysisStatus = AiAnalysisStatus.Failed;
            doc.AnalysisError = ex.Message;
            await db.SaveChangesAsync(ct);
            _log.LogWarning(ex, "Leitura da IA falhou para o documento {DocId}", docId);
        }
    }

    /// <summary>Documento cobre a subcategoria: confiança alta = Coberto, senão Parcial. Nunca rebaixa.</summary>
    private static async Task UpsertCoverageFromDocumentAsync(
        AegisScoreDbContext db, string code, Guid docId, double confidence, CancellationToken ct)
    {
        var cov = await db.SubcategoryCoverages.FirstOrDefaultAsync(c => c.SubcategoryCode == code, ct);
        var status = confidence >= 0.7 ? CoverageStatus.Coberto : CoverageStatus.Parcial;

        if (cov is null)
        {
            db.SubcategoryCoverages.Add(new SubcategoryCoverage
            {
                SubcategoryCode = code,
                Status = status,
                EvidenceSource = CoverageEvidenceSource.Document,
                OriginDocumentId = docId,
                Confidence = confidence,
                LastEvaluatedAt = DateTimeOffset.UtcNow,
            });
            return;
        }

        if (status == CoverageStatus.Coberto) cov.Status = CoverageStatus.Coberto;
        else if (cov.Status == CoverageStatus.NaoCoberto) cov.Status = CoverageStatus.Parcial;
        cov.EvidenceSource = cov.EvidenceSource == CoverageEvidenceSource.Interview
            ? CoverageEvidenceSource.Both : CoverageEvidenceSource.Document;
        cov.OriginDocumentId = docId;
        cov.Confidence = confidence;
        cov.LastEvaluatedAt = DateTimeOffset.UtcNow;
    }
}
