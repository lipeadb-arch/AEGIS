using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Application.Queries;

namespace AegisScore.Api.Controllers;

/// <summary>
/// [AEGIS-MVP-VULN-01] IDENTIFY (ID.AM / ID.RA) — leitura das VULNERABILIDADES associadas a ativos (exposição
/// ativo×CVE, modelo Microsoft Defender Vulnerability Management). Superfície DEDICADA e somente leitura: NÃO se
/// mistura com as exposições de CONFIGURAÇÃO (postura/Secure Score), servidas por /posture/exposures.
///
/// Tenant 100% implícito: o Global Query Filter escopa toda leitura ao tenant do JWT. Sem [FromHeader].
/// A coleta/reconciliação (escrita) é do pipeline de conectores; aqui nunca se cria/altera/resolve exposição.
/// </summary>
[ApiController]
[Route("api/v1/vulnerabilities")]
[Authorize]
public class VulnerabilitiesController : ControllerBase
{
    private readonly IVulnerabilityQuery _query;

    public VulnerabilitiesController(IVulnerabilityQuery query) => _query = query;

    /// <summary>Lista paginada de vulnerabilidades ativo×CVE + resumo tenant-scoped, com filtros e ordenação determinística.</summary>
    [HttpGet]
    public async Task<ActionResult<VulnerabilityListDto>> List(
        [FromQuery] string? state,
        [FromQuery] string? severity,
        [FromQuery] string? exploit,
        [FromQuery] Guid? assetId,
        [FromQuery] string? provider,
        [FromQuery] Guid? connectorId,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var filter = new VulnerabilityFilter(
            State: ParseState(state),
            Severity: string.IsNullOrWhiteSpace(severity) ? null : severity.Trim(),
            Exploit: ParseExploit(exploit),
            AssetId: assetId,
            Provider: string.IsNullOrWhiteSpace(provider) ? null : provider.Trim(),
            ConnectorId: connectorId,
            Search: string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            Page: page,
            PageSize: pageSize);

        return await _query.GetAsync(filter, ct);
    }

    /// <summary>
    /// [AEGIS-MVP-LANGUAGE-02] Visão AGRUPADA por CVE/problema — a leitura PADRÃO da tela. Paginação por GRUPO
    /// (CVE distinto), nunca por ocorrência ativo×CVE. Os ativos de um grupo carregam sob demanda em <see cref="List"/>
    /// com <c>cveId</c> exato.
    /// </summary>
    [HttpGet("overview")]
    public async Task<ActionResult<VulnerabilityOverviewDto>> Overview(
        [FromQuery] string? state,
        [FromQuery] string? severity,
        [FromQuery] string? exploit,
        [FromQuery] string? provider,
        [FromQuery] Guid? connectorId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var filter = new VulnerabilityFilter(
            State: ParseState(state),
            Severity: string.IsNullOrWhiteSpace(severity) ? null : severity.Trim(),
            Exploit: ParseExploit(exploit),
            Provider: string.IsNullOrWhiteSpace(provider) ? null : provider.Trim(),
            ConnectorId: connectorId,
            Page: page,
            PageSize: pageSize);

        return await _query.GetOverviewAsync(filter, ct);
    }

    private static VulnerabilityLifecycleFilter ParseState(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "resolved" => VulnerabilityLifecycleFilter.Resolved,
        "all" => VulnerabilityLifecycleFilter.All,
        "open" => VulnerabilityLifecycleFilter.Open,
        _ => VulnerabilityLifecycleFilter.Open,   // default: abertas
    };

    private static VulnerabilityExploitFilter ParseExploit(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "exploitable" => VulnerabilityExploitFilter.Exploitable,
        "verified" => VulnerabilityExploitFilter.Verified,
        _ => VulnerabilityExploitFilter.All,
    };
}
