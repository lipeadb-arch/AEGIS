using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Queries;

namespace AegisScore.Infrastructure.Queries;

/// <summary>
/// [AEGIS-MVP-PRIORITIES-01] Autoridade de leitura COMPOSTA da Central de Prioridades. PURA COMPOSIÇÃO das
/// três autoridades já existentes — <see cref="IWorkspacePostureQuery"/>, <see cref="IPostureExposureQuery"/>
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
    private readonly TimeProvider _clock;

    public PriorityWorkspaceQuery(
        IWorkspacePostureQuery posture,
        IPostureExposureQuery exposures,
        IVulnerabilityQuery vulnerabilities,
        TimeProvider clock)
    {
        _posture = posture;
        _exposures = exposures;
        _vulnerabilities = vulnerabilities;
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

        // Fila 2 — VULNERABILIDADES ativo×CVE: página 1, somente abertas, teto de itens; sem filtros extras.
        // A ordenação determinística (exploit verificado → público → CVSS → EPSS → criticidade) é preservada
        // exatamente pela query autoritativa. NÃO se força ordem única entre esta fila e a de exposições.
        var vulnerabilities = await _vulnerabilities.GetAsync(
            new VulnerabilityFilter(
                State: VulnerabilityLifecycleFilter.Open,
                Page: 1,
                PageSize: PriorityWorkspaceDto.MaxQueueItems),
            ct);

        return new PriorityWorkspaceDto(
            ReadModelVersion: PriorityWorkspaceDto.Version,
            GeneratedAt: _clock.GetUtcNow(),
            Posture: posture.Overall,
            ConfigurationExposures: new PriorityExposureQueueDto(exposures.Summary, exposures.Items),
            Vulnerabilities: new PriorityVulnerabilityQueueDto(vulnerabilities.Summary, vulnerabilities.Items));
    }
}
