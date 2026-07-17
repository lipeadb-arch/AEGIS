using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Queries;
using AegisScore.Application.Telemetry.Models;
using AegisScore.Domain;
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
        // Projeta as colunas no banco (inclui os blobs crus); a desserialização roda em memória — o EF não
        // traduz JSON→objeto no SQL, e o payload por tenant é pequeno. Os enums vêm CRUS (não .ToString()):
        // o status ainda decide a severidade-proxy, então precisamos dele tipado antes de achatar o DTO.
        var rows = await _db.TenantControlStates
            .AsNoTracking()
            .OrderBy(x => x.Subcategory!.Code)
            .Select(x => new Row(
                x.SubcategoryId,
                x.Subcategory!.Code,
                x.CurrentScore,
                x.Subcategory!.MaxScorePoints,   // denominador do catálogo, via JOIN — jamais desnormalizado
                x.Status,
                x.AiEvidence,
                x.LastEvaluatedAt,
                x.LastVerdictSource,
                x.ChecksJson,
                x.IntelligenceJson))
            .ToListAsync(ct);

        return rows.Select(ToDto).ToList();
    }

    /// <summary>
    /// Achata a linha crua no contrato do HUD: enums viram string na fronteira e o blob de inteligência é
    /// espalhado nos campos do DTO. O frontend recebe um objeto plano e não conhece a existência do blob.
    /// </summary>
    private static TenantControlStateDto ToDto(Row r)
    {
        var intel = SafeDeserialize<ControlIntelligence>(r.IntelligenceJson);

        return new TenantControlStateDto(
            r.SubcategoryId, r.SubcategoryCode, r.ScorePoints, r.MaxScorePoints,
            r.Status.ToString(), r.AiEvidence, r.LastEvaluatedAt, r.LastVerdictSource.ToString(),
            SafeDeserialize<IReadOnlyList<ComplianceCheck>>(r.ChecksJson) ?? Array.Empty<ComplianceCheck>())
        {
            // A severidade do motor manda; sem ela, o proxy derivado do status (o card nunca fica sem badge).
            Severity = (intel?.Severity ?? SeverityLevels.FromStatus(r.Status)).ToString(),
            TelemetryEvidence = intel?.TelemetryEvidence,
            RemediationPlan = intel?.RemediationPlan,
            AiConfidenceScore = intel?.AiConfidenceScore,
            ThreatLandscape = intel?.ThreatLandscape ?? Array.Empty<string>(),
            MttdMinutes = intel?.MttdMinutes,
            MttrMinutes = intel?.MttrMinutes,

            // ⚠️ Sem produtor: não existe snapshot POR CONTROLE (só o agregado diário do tenant, que
            // alimenta o /trend). Entregar vazio é o honesto — a sparkline se omite; sintetizar a série
            // seria forjar histórico de conformidade. Ver ComplianceHistoryPoint.
            HistoricalCompliance = Array.Empty<ComplianceHistoryPoint>(),
        };
    }

    /// <summary>
    /// Desserializa um blob persistido; tolera nulo/JSON inválido (devolve null, nunca lança). Um blob
    /// explicável corrompido não pode derrubar o dashboard inteiro — o score é a informação crítica.
    /// </summary>
    private static T? SafeDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Projeção intermediária: as colunas cruas do banco, antes da desserialização dos blobs.</summary>
    private sealed record Row(
        Guid SubcategoryId, string SubcategoryCode, int ScorePoints, int MaxScorePoints,
        ControlStatus Status, string? AiEvidence, DateTimeOffset LastEvaluatedAt, VerdictSource LastVerdictSource,
        string? ChecksJson, string? IntelligenceJson);
}
