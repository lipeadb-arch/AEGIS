using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Documents;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Documents;

/// <summary>
/// Implementação da rotina única de reconciliação documental (<see cref="IDocumentEvidenceReconciler"/>).
/// Opera SEMPRE dentro do tenant ambiente (query filter + stamping fail-closed do DbContext); o tenantId
/// explícito é asserção de defesa em profundidade.
///
/// A autoridade da evidência é o conjunto de <see cref="DocumentControlMapping"/> PROBATÓRIOS — os que
/// têm <see cref="DocumentControlMapping.EvidenceQuote"/> não-nulo (trecho literal validado em código).
/// Mappings sem trecho (herança legada/não probatória) são ignorados: não sustentam controle nem cobertura.
/// A cobertura descreve apenas a documentação disponível; não altera conformidade nem maturidade NIST.
/// </summary>
public sealed class DocumentEvidenceReconciler : IDocumentEvidenceReconciler
{
    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ILogger<DocumentEvidenceReconciler> _log;

    public DocumentEvidenceReconciler(
        AegisScoreDbContext db, ITenantContext tenant,
        ILogger<DocumentEvidenceReconciler> log)
    {
        _db = db;
        _tenant = tenant;
        _log = log;
    }

    public async Task ReconcileAsync(
        Guid tenantId, IReadOnlyCollection<string> affectedSubcategoryCodes, CancellationToken ct = default)
    {
        var ambient = _tenant.TenantId
            ?? throw new TenantSecurityException(
                "Reconciliação documental sem tenant resolvido no contexto (fail-closed).");
        if (tenantId != ambient)
            throw new TenantSecurityException(
                $"TenantId ({tenantId}) diverge do tenant do contexto ({ambient}).");

        var codes = affectedSubcategoryCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var code in codes)
        {
            // Evidência documental PROBATÓRIA vigente para o código, entre TODOS os documentos do tenant
            // (o query filter escopa ao tenant ambiente nas duas pontas do join).
            var probative = await (
                from m in _db.DocumentControlMappings
                where m.SubcategoryCode == code && m.EvidenceQuote != null
                join d in _db.GovernanceDocuments on m.GovernanceDocumentId equals d.Id
                select new ProbativeMapping(
                    m.GovernanceDocumentId, m.Confidence, m.EvidenceQuote!, m.Evidence, d.FileName, d.Title))
                .ToListAsync(ct);

            // Melhor trecho literal para descrever cobertura documental, sem atribuir maturidade.
            // Desempate estável por Id do documento.
            var bestForCoverage = probative
                .OrderByDescending(p => p.Confidence)
                .ThenBy(p => p.OriginDocumentId)
                .FirstOrDefault();

            // 2) Cobertura — reflete o melhor trecho literal (rastreabilidade), Parcial abaixo do limiar,
            //    sem apagar a evidência de entrevista.
            await ReconcileCoverageAsync(code, bestForCoverage, ct);
        }
    }

    /// <summary>
    /// Recomputa a cobertura da subcategoria: some quando não há documento nem entrevista; volta a
    /// <c>Interview</c> quando só a entrevista resta (Both sem documento); e reflete o documento vencedor
    /// quando há evidência documental — sem jamais apagar a evidência de entrevista.
    /// </summary>
    private async Task ReconcileCoverageAsync(string code, ProbativeMapping? best, CancellationToken ct)
    {
        var cov = await _db.SubcategoryCoverages.FirstOrDefaultAsync(c => c.SubcategoryCode == code, ct);

        var hasDoc = best is not null;
        var hasInterview = cov is not null
            && (cov.EvidenceSource is CoverageEvidenceSource.Interview or CoverageEvidenceSource.Both
                || cov.OriginInterviewSessionId is not null);

        // Cobertura exclusivamente documental sem documento válido DESAPARECE (nada a preservar).
        if (!hasDoc && !hasInterview)
        {
            if (cov is not null)
            {
                _db.SubcategoryCoverages.Remove(cov);
                await _db.SaveChangesAsync(ct);
            }
            return;
        }

        if (cov is null)
        {
            cov = new SubcategoryCoverage { SubcategoryCode = code };   // só alcançável com hasDoc
            _db.SubcategoryCoverages.Add(cov);
        }

        if (hasDoc)
        {
            var docStatus = best!.Confidence >= DocumentEvidencePolicy.MinConfidenceForScore
                ? CoverageStatus.Coberto
                : CoverageStatus.Parcial;

            if (hasInterview)
            {
                cov.EvidenceSource = CoverageEvidenceSource.Both;
                // Não rebaixa a força que a entrevista já garantiu (Coberto>Parcial>NaoCoberto).
                cov.Status = (CoverageStatus)Math.Max((int)cov.Status, (int)docStatus);
            }
            else
            {
                cov.EvidenceSource = CoverageEvidenceSource.Document;
                cov.Status = docStatus;
            }
            cov.OriginDocumentId = best.OriginDocumentId;
            cov.Confidence = best.Confidence;
        }
        else
        {
            // Both sem documento → Interview: descarta a parte documental, preserva a de entrevista.
            cov.EvidenceSource = CoverageEvidenceSource.Interview;
            cov.OriginDocumentId = null;
        }

        cov.LastEvaluatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Projeção de um mapping probatório vigente + metadados do documento que o originou.</summary>
    private sealed record ProbativeMapping(
        Guid OriginDocumentId, double Confidence, string EvidenceQuote, string? Rationale,
        string? FileName, string? DocTitle);
}
