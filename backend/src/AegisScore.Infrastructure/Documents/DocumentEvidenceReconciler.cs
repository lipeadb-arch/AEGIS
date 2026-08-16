using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
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
/// Mappings sem trecho (herança legada/não probatória) são ignorados: não sustentam controle, cobertura
/// nem score. A escrita no ledger é delegada ao escritor único (<see cref="IControlStateWriter"/>), que
/// preserva a precedência da telemetria; a cobertura é recomputada aqui.
/// </summary>
public sealed class DocumentEvidenceReconciler : IDocumentEvidenceReconciler
{
    /// <summary>Limiar de confiança que separa cobertura Coberto de Parcial (mesmo valor do pipeline de análise).</summary>
    private const double DocumentCoverageThreshold = 0.7;

    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IControlStateWriter _writer;
    private readonly ILogger<DocumentEvidenceReconciler> _log;

    public DocumentEvidenceReconciler(
        AegisScoreDbContext db, ITenantContext tenant, IControlStateWriter writer,
        ILogger<DocumentEvidenceReconciler> log)
    {
        _db = db;
        _tenant = tenant;
        _writer = writer;
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

            // Documento VENCEDOR determinístico: maior confiança, desempate estável por Id do documento.
            var best = probative
                .OrderByDescending(p => p.Confidence)
                .ThenBy(p => p.OriginDocumentId)
                .FirstOrDefault();

            // 1) Ledger — aplica o crédito documental vigente, ou retrai se nada sustenta. O escritor
            //    preserva integralmente um estado de telemetria.
            var documentary = best is null
                ? null
                : new DocumentaryEvidence(best.OriginDocumentId, best.Confidence, BuildEvidenceText(best));
            await _writer.ReconcileDocumentaryAsync(tenantId, code, documentary, ct);

            // 2) Cobertura — recomputada a partir da mesma evidência probatória, sem apagar entrevista.
            await ReconcileCoverageAsync(code, best, ct);
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
            var docStatus = best!.Confidence >= DocumentCoverageThreshold
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

    /// <summary>Compõe a evidência auditável do estado documental: trecho literal + racional + natureza do crédito.</summary>
    private static string BuildEvidenceText(ProbativeMapping m)
    {
        var file = !string.IsNullOrWhiteSpace(m.FileName) ? m.FileName!
                 : !string.IsNullOrWhiteSpace(m.DocTitle) ? m.DocTitle! : "documento";
        var rationale = string.IsNullOrWhiteSpace(m.Rationale) ? "" : $" Análise: {m.Rationale!.Trim()}";
        return
            $"Evidência documental ({m.Confidence:P0} de confiança) de '{file}'. " +
            $"Trecho probatório: \"{m.EvidenceQuote.Trim()}\".{rationale} " +
            "Crédito parcial (50%); conformidade plena aguarda validação por telemetria.";
    }

    /// <summary>Projeção de um mapping probatório vigente + metadados do documento que o originou.</summary>
    private sealed record ProbativeMapping(
        Guid OriginDocumentId, double Confidence, string EvidenceQuote, string? Rationale,
        string? FileName, string? DocTitle);
}
