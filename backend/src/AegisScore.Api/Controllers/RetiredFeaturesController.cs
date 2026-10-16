using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AegisScore.Api.Controllers;

/// <summary>Endereços antigos não disparam coleta nem alteram avaliações.</summary>
[ApiController]
[Authorize]
[Route("api/v1")]
public sealed class RetiredFeaturesController : ControllerBase
{
    [Route("scoring/{**path}")]
    [Route("dashboard/{**path}")]
    [Route("vulnerabilities/{**path}")]
    [Route("software-inventory/{**path}")]
    [Route("posture-exposures/{**path}")]
    [Route("detection-coverage/{**path}")]
    [Route("device-posture/{**path}")]
    [Route("priorities/{**path}")]
    [Route("telemetry/{**path}")]
    [Route("ingestion/{**path}")]
    [Route("grc/interviews/{**path}")]
    [Route("assessments/{**path}")]
    [Route("risks/{**path}")]
    public IActionResult Retired() => Problem(
        title: "Recurso aposentado",
        detail: "A plataforma Aegis Score oferece AEGIS KNIGHT e AEGIS NIST. Use /api/v1/nist/assessments ou /api/v1/knight/assessments.",
        statusCode: StatusCodes.Status410Gone);
}
