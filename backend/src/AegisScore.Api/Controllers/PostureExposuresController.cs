using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Application.Queries;

namespace AegisScore.Api.Controllers;

/// <summary>
/// [AEGIS-MVP-POSTURE-02] Superfície tenant-scoped SOMENTE LEITURA das exposições de CONFIGURAÇÃO (postura) —
/// modelo do Microsoft Secure Score. NÃO são vulnerabilidades/CVEs de ativos: são "recomendações de postura".
/// Tenant sempre IMPLÍCITO (claim <c>tenant_id</c> do JWT + Global Query Filter fail-closed) — nunca via
/// URL/QueryString, de modo que um tenant jamais leia a postura de outro. A coleta/estado das exposições é do
/// pipeline de ingestão (pull); aqui só se LÊ.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/posture")]
public class PostureExposuresController : ControllerBase
{
    private readonly IPostureExposureQuery _exposures;

    public PostureExposuresController(IPostureExposureQuery exposures) => _exposures = exposures;

    /// <summary>
    /// Lista as exposições de configuração do tenant + resumo (aberto/resolvido, distribuição por categoria,
    /// última coleta, Secure Score geral mais recente). Filtros por estado, categoria, serviço e busca; paginação;
    /// ordenação padrão pelo rank da fonte e depois pelo maior gap.
    /// </summary>
    [HttpGet("exposures")]
    public async Task<ActionResult<PostureExposureListDto>> List(
        [FromQuery] string? state = "open",
        [FromQuery] string? category = null,
        [FromQuery] string? service = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var filter = new PostureExposureFilter(
            State: ParseState(state),
            Category: category,
            Service: service,
            Search: search,
            Page: page,
            PageSize: pageSize);

        return Ok(await _exposures.GetAsync(filter, ct));
    }

    private static PostureExposureStateFilter ParseState(string? state) => (state ?? "").Trim().ToLowerInvariant() switch
    {
        "resolved" => PostureExposureStateFilter.Resolved,
        "all" => PostureExposureStateFilter.All,
        _ => PostureExposureStateFilter.Open,   // default seguro: exposições ABERTAS
    };
}
