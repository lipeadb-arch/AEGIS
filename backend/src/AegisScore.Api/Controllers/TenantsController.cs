using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Api.Controllers;

/// <summary>
/// Onboarding: cadastro do cliente, suas áreas/processos e conectores.
///
/// [Alto 3 / Médio 6] O tenant NUNCA vem da rota (IDOR latente eliminado): as escritas escopadas a um
/// tenant derivam o TenantId exclusivamente do contexto ambiente (claim do JWT, via ITenantContext), e
/// o StampTenant do DbContext revalida na gravação (fail-closed). Escritas de configuração exigem o
/// papel TenantAdmin; criar um novo tenant é operação de PLATAFORMA (PlatformAdmin), fora do fluxo de
/// um tenant comum.
/// </summary>
[ApiController]
[Route("api/v1/tenants")]
[Authorize]
public class TenantsController : ControllerBase
{
    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IConnectorSecretProtector _secrets;

    public TenantsController(
        AegisScoreDbContext db, ITenantContext tenant, IConnectorSecretProtector secrets)
    {
        _db = db;
        _tenant = tenant;
        _secrets = secrets;
    }

    /// <summary>
    /// Tenant ambiente resolvido pelo JWT. Garantido não-nulo numa rota autenticada — o
    /// TenantConsistencyMiddleware barra (403) qualquer token sem claim tenant_id válida.
    /// </summary>
    private Guid CurrentTenantId => _tenant.TenantId
        ?? throw new InvalidOperationException("Rota autenticada sem tenant no contexto.");

    /// <summary>
    /// [Alto 3] Cria um novo tenant. Operação de PLATAFORMA — exige PlatformAdmin, papel que nenhum
    /// usuário de tenant comum possui (provisionado fora do onboarding self-service).
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "PlatformAdmin")]
    public async Task<ActionResult<IdResponse>> Create(CreateTenantRequest req, CancellationToken ct)
    {
        if (await _db.Tenants.AnyAsync(x => x.Slug == req.Slug, ct))
            return Conflict($"Já existe um cliente com o slug '{req.Slug}'.");

        var t = new Tenant { Name = req.Name, Slug = req.Slug, Status = TenantStatus.Active };
        _db.Tenants.Add(t);
        await _db.SaveChangesAsync(ct);
        return new IdResponse(t.Id);
    }

    [HttpPost("business-units")]
    [Authorize(Roles = "TenantAdmin")]
    public async Task<ActionResult<IdResponse>> AddBusinessUnit(
        CreateBusinessUnitRequest req, CancellationToken ct)
    {
        var bu = new BusinessUnit
        {
            TenantId = CurrentTenantId,   // do JWT, nunca da rota; StampTenant revalida
            Name = req.Name,
            Code = req.Code,
            ManagerName = req.ManagerName,
            ManagerEmail = req.ManagerEmail,
        };
        _db.BusinessUnits.Add(bu);
        await _db.SaveChangesAsync(ct);
        return new IdResponse(bu.Id);
    }

    [HttpPost("processes")]
    [Authorize(Roles = "TenantAdmin")]
    public async Task<ActionResult<IdResponse>> AddProcess(
        CreateProcessRequest req, CancellationToken ct)
    {
        var p = new BusinessProcess
        {
            TenantId = CurrentTenantId,
            Name = req.Name,
            ProcessCategory = req.ProcessCategory,
            Classification = req.Classification,
            ProcessValue = req.ProcessValue,
        };
        _db.Processes.Add(p);
        await _db.SaveChangesAsync(ct);
        return new IdResponse(p.Id);
    }

    [HttpPost("connectors")]
    [Authorize(Roles = "TenantAdmin")]
    public async Task<ActionResult<IdResponse>> AddConnector(
        CreateConnectorRequest req, CancellationToken ct)
    {
        var c = new ConnectorConfig
        {
            TenantId = CurrentTenantId,
            Provider = req.Provider,
            Capability = req.Capability,
            DisplayName = req.DisplayName,
            AuthType = req.AuthType,
            // [Médio 6/Baixo] Segredo do conector (tokens OAuth, API keys) é cifrado NO SERVIDOR via
            // Data Protection — nunca confiado pré-cifrado do cliente. Em claro só trafega dentro do
            // TLS; no banco fica cifrado. A decifragem ocorre no momento da coleta (fase de conectores).
            EncryptedSettings = _secrets.Protect(req.Settings),
            SyncIntervalMinutes = req.SyncIntervalMinutes,
        };
        _db.Connectors.Add(c);
        await _db.SaveChangesAsync(ct);
        return new IdResponse(c.Id);
    }
}
