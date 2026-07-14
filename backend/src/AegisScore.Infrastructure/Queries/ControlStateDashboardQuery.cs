using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Queries;
using AegisScore.Application.Telemetry.Models;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Queries;

/// <summary>
/// Lê a matriz de conformidade do tenant sobre o AegisScoreDbContext.
///
/// Zero Trust: NÃO há <c>.Where(x => x.TenantId == ...)</c>. O Global Query Filter (fail-closed) já
/// restringe TenantControlStates ao tenant ambiente resolvido do JWT — delegar o recorte ao filtro É o
/// próprio isolamento. Um <c>Where</c> explícito seria pior: daria a falsa impressão de que a segurança
/// depende desta linha e não do DbContext, e mascararia um filtro removido por acidente.
///
/// Performance: <c>AsNoTracking</c> (leitura pura, sem change tracker) e projeção direta no banco — o
/// SELECT carrega apenas as 7 colunas do DTO, nunca a entidade inteira nem o grafo da subcategoria.
/// </summary>
public sealed class ControlStateDashboardQuery : IControlStateDashboardQuery
{
    private readonly AegisScoreDbContext _db;

    public ControlStateDashboardQuery(AegisScoreDbContext db) => _db = db;

    public async Task<IReadOnlyList<TenantControlStateDto>> GetDashboardAsync(CancellationToken ct = default)
    {
        // Projeta as colunas no banco (inclui o ChecksJson cru); a desserialização do checklist roda em
        // memória — o EF não traduz JSON→objeto no SQL, e o payload por tenant é pequeno.
        var rows = await _db.TenantControlStates
            .AsNoTracking()
            .OrderBy(x => x.Subcategory!.Code)
            .Select(x => new Row(
                x.SubcategoryId,
                x.Subcategory!.Code,
                x.CurrentScore,
                x.Subcategory!.MaxScorePoints,   // denominador do catálogo, via JOIN — jamais desnormalizado
                x.Status.ToString(),
                x.AiEvidence,
                x.LastEvaluatedAt,
                x.LastVerdictSource.ToString(),
                x.ChecksJson))
            .ToListAsync(ct);

        return rows.Select(r => new TenantControlStateDto(
            r.SubcategoryId, r.SubcategoryCode, r.ScorePoints, r.MaxScorePoints,
            r.ControlStatus, r.AiEvidence, r.LastEvaluatedAt, r.LastVerdictSource,
            DeserializeChecks(r.ChecksJson))).ToList();
    }

    /// <summary>Desserializa o checklist persistido; tolera nulo/JSON inválido (devolve vazio, nunca lança).</summary>
    private static IReadOnlyList<ComplianceCheck> DeserializeChecks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<ComplianceCheck>();
        try { return JsonSerializer.Deserialize<IReadOnlyList<ComplianceCheck>>(json) ?? Array.Empty<ComplianceCheck>(); }
        catch (JsonException) { return Array.Empty<ComplianceCheck>(); }
    }

    /// <summary>Projeção intermediária: as colunas cruas do banco, antes da desserialização do checklist.</summary>
    private sealed record Row(
        Guid SubcategoryId, string SubcategoryCode, int ScorePoints, int MaxScorePoints,
        string ControlStatus, string? AiEvidence, DateTimeOffset LastEvaluatedAt, string LastVerdictSource, string? ChecksJson);
}
