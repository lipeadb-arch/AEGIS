namespace AegisScore.Application.Queries;

/// <summary>
/// Estado atual de UM controle NIST do tenant, achatado para consumo do HUD. É um contrato de leitura:
/// o frontend jamais recebe a entidade de domínio (<c>TenantControlState</c>) crua, o que nos deixa
/// evoluir o modelo sem quebrar o Angular — e impede que campos internos vazem por acidente.
///
/// Enums viram <c>string</c> na fronteira ("Compliant", "Telemetry"): um cliente TypeScript não deve
/// depender do valor numérico de um enum C#, que muda ao reordenar o domínio.
/// </summary>
/// <param name="SubcategoryCode">Código NIST ("PR.AA-01") — o identificador que o HUD exibe. Vem do
/// mesmo JOIN que traz o peso, portanto não custa nada e evita uma segunda chamada ao catálogo.</param>
/// <param name="ScorePoints">Pontos obtidos (numerador do Aegis Score).</param>
/// <param name="MaxScorePoints">Peso da subcategoria no catálogo (denominador) — nunca do estado do tenant.</param>
/// <param name="LastVerdictSource">Procedência do veredito vigente: "Telemetry" (autoritativa) ou
/// "Documentary" (crédito parcial). É o que permite ao HUD rotular "50% (Documentado)".</param>
public record TenantControlStateDto(
    Guid SubcategoryId,
    string SubcategoryCode,
    int ScorePoints,
    int MaxScorePoints,
    string ControlStatus,
    string? AiEvidence,
    DateTimeOffset LastEvaluatedAt,
    string LastVerdictSource);

/// <summary>
/// Consulta de leitura do estado de conformidade de TODOS os controles do tenant — a matriz que alimenta
/// o HUD de scoring. O CONTRATO vive na Application (que não conhece EF Core); a implementação sobre o
/// AegisScoreDbContext mora na Infrastructure — mesmo padrão porta/adaptador de
/// <see cref="ICurrentScoreQuery"/>, <see cref="ITenantScoreTrendQuery"/> e <see cref="IGetPendingControlsQuery"/>.
///
/// Zero Trust: o tenant NÃO é parâmetro. O isolamento é fail-closed via ITenantContext + Global Query
/// Filter, de modo que a consulta enxerga exclusivamente o tenant resolvido do claim <c>tenant_id</c>.
/// </summary>
public interface IControlStateDashboardQuery
{
    /// <summary>Estado de cada controle avaliado do tenant ambiente, ordenado pelo código NIST.</summary>
    Task<IReadOnlyList<TenantControlStateDto>> GetDashboardAsync(CancellationToken ct = default);
}
