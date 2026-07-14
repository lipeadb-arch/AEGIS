using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Scoring;

namespace AegisScore.Api.Workers;

/// <summary>
/// Consome a fila de leitura: para cada documento, extrai o texto, chama a IA para mapear os
/// controles NIST e atualiza o ledger de cobertura. Roda sob um SystemTenantContext do tenant
/// dono do documento (o stamping é fail-closed e não há header de request aqui).
/// </summary>
public sealed class DocumentAnalysisWorker : BackgroundService
{
    /// <summary>Limiar de confiança da IA que separa cobertura Coberta de Parcial no ledger de cobertura.</summary>
    private const double DocumentCoverageThreshold = 0.7;

    /// <summary>
    /// Teto de crédito da evidência PURAMENTE DOCUMENTAL no Aegis Score. Um documento atesta processo e
    /// intenção — nunca prova que o controle está tecnicamente implementado. Por isso vale 50% dos pontos
    /// (<see cref="ControlStatus.MitigatedByThirdParty"/>) e JAMAIS <see cref="ControlStatus.Compliant"/>,
    /// que fica reservado à validação por telemetria. É o que separa conformidade real de teatro de
    /// segurança: nenhum PDF pode, sozinho, produzir um painel de "100% seguro".
    /// </summary>
    private const ControlStatus DocumentEvidenceStatus = ControlStatus.MitigatedByThirdParty;

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

        // O writer precisa do MESMO tenant ambiente do DbContext. Fora de um request HTTP não há
        // ITenantContext utilizável no container (o HttpTenantContext devolveria null e o guard
        // fail-closed abortaria), então o construímos com o SystemTenantContext do documento —
        // exatamente como já fazemos com o DbContext. A dependência declarada é a PORTA.
        var tenantCtx = new SystemTenantContext(tenantId);
        await using var db = new AegisScoreDbContext(options, tenantCtx);
        IControlStateWriter writer = new ControlStateWriter(
            db, tenantCtx, sp.GetRequiredService<ILogger<ControlStateWriter>>());

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

            // Ponte Govern → Aegis Score. Roda DEPOIS do commit da trilha documental: a análise é atômica
            // em si, e a projeção no ledger é um passo idempotente (upsert) — reprocessar o documento
            // reescreve a mesma célula, nunca duplica. Sem acoplamento ao motor de telemetria: ambas as
            // fontes de evidência gravam pelo mesmo IControlStateWriter.
            await ProjectToScoreAsync(writer, tenantId, analysis.Claims, doc.FileName, ct);
        }
        catch (Exception ex)
        {
            doc.AnalysisStatus = AiAnalysisStatus.Failed;
            doc.AnalysisError = ex.Message;
            await db.SaveChangesAsync(ct);
            _log.LogWarning(ex, "Leitura da IA falhou para o documento {DocId}", docId);
        }
    }

    /// <summary>
    /// Projeta cada controle mapeado pelo documento no ledger de conformidade (TenantControlState),
    /// através da porta <see cref="IControlStateWriter"/> — a mesma usada pelo motor de telemetria.
    /// Um código alucinado pelo LLM (fora do catálogo NIST) é registrado e ignorado: nunca aborta os
    /// demais claims nem o documento, que já está persistido como Analyzed.
    /// </summary>
    private async Task ProjectToScoreAsync(
        IControlStateWriter writer, Guid tenantId, IReadOnlyList<DocumentClaim> claims,
        string? fileName, CancellationToken ct)
    {
        foreach (var claim in claims)
        {
            // A confiança da IA NÃO eleva o status — evidência documental tem teto de 50%. Ela é
            // preservada na trilha de auditoria, que registra origem, racional e a natureza parcial
            // do crédito concedido.
            var evidence =
                $"Evidência documental ({claim.Confidence:P0} de confiança) extraída de " +
                $"'{fileName ?? "documento"}': {claim.Claim} " +
                $"— crédito parcial (50%); conformidade plena aguarda validação por telemetria.";

            try
            {
                // Documentary: o writer só aplica se este veredito PONTUAR MAIS que o estado vigente.
                // Um controle já validado por telemetria (Compliant) nunca é rebaixado por um PDF.
                await writer.ApplyVerdictAsync(
                    tenantId, claim.SubcategoryCode, DocumentEvidenceStatus, evidence, VerdictSource.Documentary, ct: ct);
            }
            catch (InvalidOperationException ex)
            {
                _log.LogWarning(ex,
                    "Claim ignorado: a subcategoria '{Code}' não existe no catálogo NIST (documento {File}).",
                    claim.SubcategoryCode, fileName);
            }
        }
    }

    /// <summary>Documento cobre a subcategoria: confiança alta = Coberto, senão Parcial. Nunca rebaixa.</summary>
    private static async Task UpsertCoverageFromDocumentAsync(
        AegisScoreDbContext db, string code, Guid docId, double confidence, CancellationToken ct)
    {
        var cov = await db.SubcategoryCoverages.FirstOrDefaultAsync(c => c.SubcategoryCode == code, ct);
        var status = confidence >= DocumentCoverageThreshold ? CoverageStatus.Coberto : CoverageStatus.Parcial;

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
