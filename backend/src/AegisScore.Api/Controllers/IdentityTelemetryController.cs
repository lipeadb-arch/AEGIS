using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Identity;

namespace AegisScore.Api.Controllers;

/// <summary>
/// [AEGIS-MVP-EVIDENCE-FABRIC-01] Postura de EVIDÊNCIA de identidade (Microsoft Entra ID). Superfície de
/// LEITURA/COLETA da Evidence Fabric compartilhada: NÃO existe mais uma segunda integração Entra com números
/// simulados nem um mapeamento hardcoded para PR.AA-01/GV.RR-01. Tanto esta rota quanto o botão de coleta do
/// AEGIS KNIGHT convergem para o MESMO caso de uso (<see cref="IIdentityEvidenceService"/>) — uma aquisição
/// real por operação lógica, sem um segundo cliente Graph, credencial ou consulta duplicada.
///
/// A resposta é CONSULTIVA: separa explicitamente o estado do conector, o da coleta e a evidência POR
/// controle NIST — e reconhece que a telemetria coletada, embora presente, é INSUFICIENTE para avaliar o
/// requisito dos controles de identidade (PR.AA-01, PR.AA-03, GV.RR-01). NUNCA grava veredito no ledger,
/// NUNCA concede pontos ao AEGIS Score. Tenant IMPLÍCITO do claim <c>tenant_id</c> do JWT (Zero Trust).
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/telemetry/identity")]
public class IdentityTelemetryController : ControllerBase
{
    private readonly IIdentityEvidenceService _evidence;
    private readonly ITenantContext _tenant;

    public IdentityTelemetryController(IIdentityEvidenceService evidence, ITenantContext tenant)
    {
        _evidence = evidence;
        _tenant = tenant;
    }

    /// <summary>
    /// Lê a postura de evidência de identidade do ÚLTIMO snapshot compartilhado (sem nova aquisição): estado do
    /// conector, estado da coleta (completa/parcial/nunca), degradação, fonte/freshness reais e a evidência por
    /// controle NIST. Não consulta o Graph e não altera score.
    /// </summary>
    /// <response code="200">Projeção consultiva da evidência de identidade (sem score).</response>
    /// <response code="401">Tenant não resolvido no contexto (claim tenant_id ausente).</response>
    [HttpGet("entra-id")]
    public async Task<ActionResult<IdentityEvidenceProjectionDto>> GetEntraId(CancellationToken ct)
    {
        if (_tenant.TenantId is null)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");

        var projection = await _evidence.GetLatestProjectionAsync(ct);
        return Ok(ToDto(projection));
    }

    /// <summary>
    /// Dispara UMA aquisição real da postura de identidade do Entra ID pela Evidence Fabric compartilhada,
    /// persiste o snapshot normalizado (tenant-safe, com proveniência/completude) e devolve a projeção
    /// consultiva resultante. Uma coleta que falhe NÃO apaga a última evidência válida — sinaliza a degradação.
    /// Recusa conector desabilitado/sem credencial devolvendo o estado do conector. NÃO grava no ledger.
    /// </summary>
    /// <response code="200">Projeção consultiva após a aquisição (sem score).</response>
    /// <response code="401">Tenant não resolvido no contexto (claim tenant_id ausente).</response>
    [HttpPost("entra-id")]
    public async Task<ActionResult<IdentityEvidenceProjectionDto>> CollectEntraId(CancellationToken ct)
    {
        if (_tenant.TenantId is null)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");

        var acquisition = await _evidence.CollectAsync(ct);
        var projection = IdentityEvidenceProjection.Build(acquisition.ConnectorState, acquisition.Snapshot);
        return Ok(ToDto(projection));
    }

    private static IdentityEvidenceProjectionDto ToDto(IdentityEvidenceProjection p) => new(
        p.ConnectorState.ToString(),
        p.CollectionState.ToString(),
        p.LastAttemptState.ToString(),
        p.IsDegraded,
        p.Source,
        p.SchemaVersion,
        p.CollectedAt,
        p.LastAttemptAt,
        p.LastAttemptDetail,
        p.Capabilities.Select(c => new IdentityCapabilityDto(c.Capability.ToString(), c.Outcome.ToString(), c.Detail)).ToList(),
        p.Controls.Select(c => new IdentityControlEvidenceDto(c.Code, c.Title, c.State.ToString(), c.Explanation)).ToList(),
        ToDto(p.IdentityRisk),
        ToDto(p.AuthenticationPosture));

    // ---- [AEGIS-MVP-MICROSOFT-COVERAGE-03] Risco de identidade -------------------------------------
    // Projeção pura de AGREGADOS. Nenhum campo pessoal existe do lado de cá para ser mapeado: o que não foi
    // coletado vira `null` (a UI mostra "não coletado"), jamais um zero que sugira ausência de risco.

    private static IdentityRiskDto? ToDto(IdentityRiskPosture? risk) => risk is null ? null : new(
        new IdentityRiskCapabilityDto(
            risk.RiskyUsersOutcome.ToString(), risk.RiskyUsersDetail,
            risk.RiskyUsers is not null, risk.RiskyUsers?.IsComplete ?? false),
        new IdentityRiskCapabilityDto(
            risk.RiskDetectionsOutcome.ToString(), risk.RiskDetectionsDetail,
            risk.RiskDetections is not null, risk.RiskDetections?.IsComplete ?? false),
        risk.RiskyUsers is null ? null : new IdentityRiskyUsersDto(
            risk.RiskyUsers.Total,
            risk.RiskyUsers.Deleted,
            risk.RiskyUsers.Processing,
            risk.RiskyUsers.Live,
            risk.RiskyUsers.Active,
            risk.RiskyUsers.HighRiskActive,
            ToDto(risk.RiskyUsers.Levels),
            ToDto(risk.RiskyUsers.States),
            risk.RiskyUsers.MostRecentRiskUpdateAt,
            risk.RiskyUsers.IsComplete),
        risk.RiskDetections is null ? null : new IdentityRiskDetectionsDto(
            risk.RiskDetections.WindowDays,
            risk.RiskDetections.WindowStart,
            risk.RiskDetections.WindowEnd,
            risk.RiskDetections.TotalInWindow,
            risk.RiskDetections.OutsideWindow,
            risk.RiskDetections.Undated,
            risk.RiskDetections.InRecentWindow,
            risk.RiskDetections.Active,
            risk.RiskDetections.Resolved,
            risk.RiskDetections.HighRiskActive,
            risk.RiskDetections.PremiumDetailWithheld,
            risk.RiskDetections.Realtime,
            risk.RiskDetections.NearRealtime,
            risk.RiskDetections.Offline,
            risk.RiskDetections.TimingNotDefined,
            risk.RiskDetections.TimingUnknown,
            ToDto(risk.RiskDetections.Levels),
            ToDto(risk.RiskDetections.States),
            risk.RiskDetections.TopTypes.Select(t => new IdentityRiskCategoryDto(t.Category, t.Count)).ToList(),
            risk.RiskDetections.MostRecentDetectionAt,
            risk.RiskDetections.IsComplete),
        risk.EvaluatedAt);

    private static IdentityAuthenticationPostureDto? ToDto(IdentityAuthenticationPosture? posture) =>
        posture is null ? null : new(
            posture.TotalUsers,
            posture.MfaCapable,
            posture.MfaRegistered,
            posture.PasswordlessCapable,
            posture.CapabilityUnknown,
            posture.MfaCapableCoveragePercent,
            posture.PasswordlessCoveragePercent,
            posture.MethodsRegistered.Select(m => new IdentityRiskCategoryDto(m.Category, m.Count)).ToList(),
            posture.IsComplete);

    private static IdentityRiskLevelsDto ToDto(IdentityRiskLevelDistribution d) =>
        new(d.High, d.Medium, d.Low, d.None, d.Hidden, d.Unknown);

    private static IdentityRiskStatesDto ToDto(IdentityRiskStateDistribution d) =>
        new(d.AtRisk, d.ConfirmedCompromised, d.Remediated, d.Dismissed, d.ConfirmedSafe, d.None, d.Unknown,
            d.Active, d.Resolved);
}
