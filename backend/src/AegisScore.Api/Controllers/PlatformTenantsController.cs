using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Api.Auth;
using AegisScore.Api.Contracts;
using AegisScore.Application.Services;
using AegisScore.Infrastructure.Auth;

namespace AegisScore.Api.Controllers;

/// <summary>
/// [AEGIS-MVP-ADMIN-LIFECYCLE-01] Superfície de PLATAFORMA para o ciclo de vida dos tenants: catálogo,
/// renomeação do nome de exibição, suspensão e reativação. É a contrapartida administrativa do
/// <see cref="TenantsController"/> (que só CRIA um tenant, no fluxo de onboarding do próprio criador).
///
/// [AEGIS-AUD-011] Protegida pela POLICY global <see cref="PlatformAuthorization.PolicyName"/> (claim
/// <c>platform_role = PlatformAdmin</c>) — nenhum papel de tenant a alcança. Como a policy barra qualquer não
/// autorizado ANTES da ação, os desfechos (incl. 404) só são vistos por quem tem autoridade de plataforma; a
/// existência/os dados de um tenant nunca vazam a quem não a possui.
///
/// ⚠️ Fronteiras deliberadas: NÃO altera slug (imutável), NÃO exclui fisicamente tenant (histórico preservado)
/// e NÃO toca score/ledger/NIST. A autoridade do ATOR vem SEMPRE do JWT (claim <c>account_id</c>/policy),
/// nunca do corpo nem de um identificador manipulável.
/// </summary>
[ApiController]
[Route("api/v1/platform/tenants")]
[Authorize(Policy = PlatformAuthorization.PolicyName)]
public sealed class PlatformTenantsController : ControllerBase
{
    private readonly IPlatformTenantAdminService _tenants;

    public PlatformTenantsController(IPlatformTenantAdminService tenants) => _tenants = tenants;

    /// <summary>Catálogo COMPLETO de tenants (inclusive suspensos), somente leitura. Ordem estável por nome.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TenantAdminDto>>> List(CancellationToken ct)
    {
        var tenants = await _tenants.ListTenantsAsync(ct);
        return Ok(tenants.Select(ToDto).ToList());
    }

    /// <summary>
    /// Renomeia o NOME DE EXIBIÇÃO de um tenant. 200 (renomeado) · 404 (inexistente) · 400 (nome inválido). O
    /// slug é imutável — não trafega no corpo.
    /// </summary>
    [HttpPut("{tenantId:guid}")]
    public async Task<ActionResult<TenantAdminDto>> Rename(
        Guid tenantId, RenameTenantRequest req, CancellationToken ct)
    {
        var result = await _tenants.RenameTenantAsync(new RenameTenantCommand(tenantId, req.Name), ct);
        return Respond(result);
    }

    /// <summary>
    /// Suspende um tenant (preserva histórico e configurações; impede o uso operacional e revoga as sessões
    /// ativas do ambiente). 200 · 404 · 409 (deixaria o próprio administrador sem ambiente de recuperação).
    /// </summary>
    [HttpPost("{tenantId:guid}/suspend")]
    public async Task<ActionResult<TenantAdminDto>> Suspend(Guid tenantId, CancellationToken ct)
    {
        if (!TryGetAccountId(out var actorAccountId))
            return Unauthorized(new { title = "Token sem conta de identidade.", status = 401 });

        var result = await _tenants.SetTenantStatusAsync(
            new SetTenantStatusCommand(tenantId, Suspend: true, actorAccountId), ct);
        return Respond(result);
    }

    /// <summary>Reativa um tenant suspenso (→ Active). Idempotente; não restaura sessões antigas. 200 · 404.</summary>
    [HttpPost("{tenantId:guid}/reactivate")]
    public async Task<ActionResult<TenantAdminDto>> Reactivate(Guid tenantId, CancellationToken ct)
    {
        if (!TryGetAccountId(out var actorAccountId))
            return Unauthorized(new { title = "Token sem conta de identidade.", status = 401 });

        var result = await _tenants.SetTenantStatusAsync(
            new SetTenantStatusCommand(tenantId, Suspend: false, actorAccountId), ct);
        return Respond(result);
    }

    /// <summary>Traduz o desfecho da mutação em HTTP. A cópia de validação vem do serviço (dono da política).</summary>
    private ActionResult<TenantAdminDto> Respond(TenantAdminMutationResult result) => result.Status switch
    {
        TenantAdminMutationStatus.Updated => Ok(ToDto(result.Tenant!)),

        TenantAdminMutationStatus.NotFound =>
            NotFound(new { title = "Ambiente não encontrado.", status = 404 }),

        TenantAdminMutationStatus.InvalidName =>
            BadRequest(new { title = result.Detail ?? "Nome inválido.", status = 400 }),

        // Conflito de INVARIANTE (auto-lockout): 409 com mensagem orientada à operação.
        TenantAdminMutationStatus.SelfLockoutForbidden =>
            Conflict(new { title = result.Detail ?? "Operação não permitida.", status = 409 }),

        _ => BadRequest(new { title = "Requisição inválida.", status = 400 }),
    };

    /// <summary>Lê a PESSOA autenticada da claim <c>account_id</c> — nunca de um id do corpo (que se forjaria).</summary>
    private bool TryGetAccountId(out Guid accountId) =>
        Guid.TryParse(User.FindFirst(JwtTokenService.AccountClaim)?.Value, out accountId) && accountId != Guid.Empty;

    private static TenantAdminDto ToDto(TenantAdminSummary t) =>
        new(t.Id, t.Name, t.Slug, t.Status.ToString(), t.CreatedAt, t.UpdatedAt);
}
