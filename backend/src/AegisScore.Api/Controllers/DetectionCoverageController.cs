using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Application.Queries;

namespace AegisScore.Api.Controllers;

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-02] Superfície tenant-scoped SOMENTE LEITURA da COBERTURA DE DETECÇÃO (regras do SIEM
/// × MITRE ATT&CK). É CONSULTIVA: mostra o que está configurado no SIEM, não comprova eficácia e NÃO altera o AEGIS
/// Score, a conformidade NIST nem os estados determinísticos dos controles. Tenant sempre IMPLÍCITO (claim
/// <c>tenant_id</c> do JWT + Global Query Filter fail-closed) — nunca via URL/QueryString. A coleta é do pipeline
/// de ingestão (pull); aqui só se LÊ o snapshot atual (agregados seguros — nunca nome/texto de regra ou credencial).
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/detection-coverage")]
public class DetectionCoverageController : ControllerBase
{
    private readonly IDetectionCoverageQuery _coverage;

    public DetectionCoverageController(IDetectionCoverageQuery coverage) => _coverage = coverage;

    /// <summary>Cobertura de detecção atual do tenant: fonte, versão ATT&CK, estado/completude, totais e técnicas agregadas.</summary>
    [HttpGet]
    public async Task<ActionResult<DetectionCoverageViewDto>> Get(CancellationToken ct = default) =>
        Ok(await _coverage.GetAsync(ct));
}
