using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Queries;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Queries;

/// <summary>
/// [AEGIS-MVP-PRIORITIES-01] Leitura COMPOSTA da Central de Prioridades — NÃO uma nova autoridade de decisão,
/// e sim PURA COMPOSIÇÃO das três autoridades já existentes — <see cref="IWorkspacePostureQuery"/>, <see cref="IPostureExposureQuery"/>
/// e <see cref="IVulnerabilityQuery"/>: reúne postura, exposições de configuração e vulnerabilidades numa
/// única leitura SEM recalcular score, gap, rank, lifecycle, CVSS, EPSS, criticidade ou postura, e SEM
/// combinar as dimensões num índice único (as duas filas permanecem separadas).
///
/// As três queries são SCOPED e compartilham o mesmo <c>AegisScoreDbContext</c> por requisição — por isso as
/// chamadas são SEQUENCIAIS (um DbContext não é thread-safe; nada de <c>Task.WhenAll</c> aqui). O tenant é
/// IMPLÍCITO: esta composição não lê tenant algum — a isolação vem por construção das queries tenant-scoped
/// (ITenantContext + Global Query Filter fail-closed). O relógio é injetável (<see cref="TimeProvider"/>).
/// </summary>
public sealed class PriorityWorkspaceQuery : IPriorityWorkspaceQuery
{
    private readonly IWorkspacePostureQuery _posture;
    private readonly IPostureExposureQuery _exposures;
    private readonly IVulnerabilityQuery _vulnerabilities;
    private readonly IAegisKnightAssessmentService _knight;
    private readonly TimeProvider _clock;

    public PriorityWorkspaceQuery(
        IWorkspacePostureQuery posture,
        IPostureExposureQuery exposures,
        IVulnerabilityQuery vulnerabilities,
        IAegisKnightAssessmentService knight,
        TimeProvider clock)
    {
        _posture = posture;
        _exposures = exposures;
        _vulnerabilities = vulnerabilities;
        _knight = knight;
        _clock = clock;
    }

    public async Task<PriorityWorkspaceDto> GetAsync(CancellationToken ct = default)
    {
        // Postura consolidada atual — mesma projeção do Dashboard/Funções (nada é recalculado aqui).
        var posture = await _posture.GetAsync(ct);

        // Fila 1 — exposições de CONFIGURAÇÃO: página 1, somente abertas, teto de itens; sem filtros extras.
        // A ordenação (rank da fonte, depois maior gap) é preservada exatamente pela query autoritativa.
        var exposures = await _exposures.GetAsync(
            new PostureExposureFilter(
                State: PostureExposureStateFilter.Open,
                Page: 1,
                PageSize: PriorityWorkspaceDto.MaxQueueItems),
            ct);

        // Fila 2 — VULNERABILIDADES por GRUPO/CVE (não ocorrências ativo×CVE repetidas): página 1, somente
        // abertas, teto de itens. A ordenação determinística (grupo aberto → exploit → CVSS → EPSS → criticidade
        // → nº de ativos) é preservada exatamente pela query autoritativa. NÃO se força ordem única entre as filas.
        var vulnerabilities = await _vulnerabilities.GetOverviewAsync(
            new VulnerabilityFilter(
                State: VulnerabilityLifecycleFilter.Open,
                Page: 1,
                PageSize: PriorityWorkspaceDto.MaxQueueItems),
            ct);

        // Fila 3 — ACHADOS DE IDENTIDADE: leitura da avaliação KNIGHT já persistida (a MESMA que a tela do
        // KNIGHT mostra). Somente leitura: NÃO dispara coleta, NÃO reavalia e NÃO recalcula score — abrir a
        // Central jamais toca a fonte de identidade.
        var knight = await _knight.GetLatestAsync(ct);

        return new PriorityWorkspaceDto(
            ReadModelVersion: PriorityWorkspaceDto.Version,
            GeneratedAt: _clock.GetUtcNow(),
            Posture: posture.Overall,
            ConfigurationExposures: new PriorityExposureQueueDto(exposures.Summary, exposures.Items),
            Vulnerabilities: new PriorityVulnerabilityQueueDto(vulnerabilities.Summary, vulnerabilities.Groups),
            IdentityFindings: BuildKnightQueue(knight));
    }

    /// <summary>
    /// Projeta a avaliação KNIGHT na fila da Central SEM duplicar sua lógica: severidade, veredito, evidência,
    /// contagem de afetados, fonte e data saem VERBATIM da autoridade. A única decisão daqui é de APRESENTAÇÃO
    /// — quais itens entram na fila curta (os EXPOSTOS) e em que ordem (severidade, depois o identificador do
    /// indicador, empate estável). Nada é recalculado, reponderado ou combinado com as outras filas.
    ///
    /// "Não avaliado" NÃO entra na fila como se fosse achado, mas a quantidade viaja no cabeçalho: cobertura
    /// incompleta é informação, não silêncio.
    /// </summary>
    private static PriorityKnightQueueDto BuildKnightQueue(KnightAssessment? a)
    {
        if (a is null)
            return new PriorityKnightQueueDto(
                null, false, null, null, null, null, null, 0, 0, 0,
                Array.Empty<PriorityKnightFindingDto>());

        var top = a.Indicators
            .Where(i => i.Status == KnightIndicatorStatus.Exposed)
            .OrderBy(i => SeverityRank(i.Severity))
            .ThenBy(i => i.IndicatorId, StringComparer.Ordinal)
            .Take(PriorityWorkspaceDto.MaxQueueItems)
            .Select(i => new PriorityKnightFindingDto(
                i.IndicatorId, i.Title, i.Category.ToString(), i.Severity.ToString(), i.Status.ToString(),
                i.Evidence, i.AffectedObjectCount, i.HasAffectedDetail, i.CollectedAt))
            .ToList();

        return new PriorityKnightQueueDto(
            RunId: a.Id,
            IsDemo: a.SourceType == KnightSourceType.Demo,
            SourceLabel: a.Source,
            SourceType: a.SourceType.ToString(),
            SourceState: a.SourceState.ToString(),
            CollectedAt: a.CompletedAt ?? a.StartedAt,
            Score: a.Score,
            Coverage: a.Coverage,
            ExposedCount: a.ExposedCount,
            NotEvaluatedCount: a.NotEvaluatedCount,
            Top: top);
    }

    /// <summary>Ordem de exibição da régua ÚNICA de severidade do produto — não é um peso de score.</summary>
    private static int SeverityRank(SeverityLevel severity) => severity switch
    {
        SeverityLevel.Critical => 0,
        SeverityLevel.High => 1,
        SeverityLevel.Medium => 2,
        SeverityLevel.Low => 3,
        _ => 4,
    };
}
