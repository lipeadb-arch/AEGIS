using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Ai;

/// <summary>Contexto dos assessments KNIGHT/NIST, documentos e inventário; sem uma terceira pontuação.</summary>
public sealed class AuditorContextBuilder : IAuditorContextBuilder
{
    private readonly AegisScoreDbContext _db;
    private readonly IAuditorAssessmentContextBuilder _assessments;

    public AuditorContextBuilder(AegisScoreDbContext db, IAuditorAssessmentContextBuilder assessments)
    {
        _db = db;
        _assessments = assessments;
    }

    public async Task<AuditorTenantContext> BuildAsync(AuditorFocus focus, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(focus);
        // Valida a seleção e o tenant antes de carregar qualquer evidência.
        var assessments = await _assessments.BuildAsync(focus, ct);
        var context = await BuildAsync(ct);
        return context with { Assessments = assessments };
    }

    public async Task<AuditorTenantContext> BuildAsync(CancellationToken ct = default)
    {
        var rows = await (from m in _db.DocumentControlMappings.AsNoTracking()
                          join d in _db.GovernanceDocuments.AsNoTracking() on m.GovernanceDocumentId equals d.Id
                          where m.EvidenceQuote != null && m.EvidenceQuote != ""
                          orderby m.Confidence descending
                          select new { d.Title, m.SubcategoryCode, m.Confidence, m.EvidenceQuote })
                         .Take(8).ToListAsync(ct);
        var evidence = rows.Select(e => new AuditorDocumentEvidence(e.Title, e.SubcategoryCode, e.Confidence,
            e.EvidenceQuote!.Length <= 240 ? e.EvidenceQuote : e.EvidenceQuote[..240] + "…")).ToList();
        var sources = await _db.Connectors.AsNoTracking().ToListAsync(ct);
        sources = sources.Where(c => ProductConnectorScope.Supports(c.Provider, c.Capability)).ToList();
        var connectors = new AuditorConnectorContext(sources.Count, sources.Count(c => c.Enabled),
            sources.Count(c => c.LastStatus == AegisScore.Domain.ConnectorStatus.Healthy),
            sources.Count(c => c.LastStatus == AegisScore.Domain.ConnectorStatus.Degraded),
            sources.Count(c => c.LastStatus == AegisScore.Domain.ConnectorStatus.Failed),
            sources.Count(c => c.LastSyncAt == null), sources.Max(c => c.LastSyncAt));
        // Campos da versão anterior permanecem neutros por compatibilidade de contrato.
        // As notas reais, evidências e limitações vêm de Assessments, com seleção identificada.
        return new AuditorTenantContext("Retired", null, 0, 0, 0, 0, 0, null,
            Array.Empty<AuditorFunctionPosture>(), Array.Empty<AuditorControlGap>(), evidence, connectors,
            Array.Empty<string>());
    }
}
