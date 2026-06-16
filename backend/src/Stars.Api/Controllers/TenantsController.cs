using Microsoft.AspNetCore.Mvc;
using Stars.Api.Contracts;
using Stars.Domain;
using Stars.Infrastructure.Persistence;

namespace Stars.Api.Controllers;

/// <summary>Onboarding: register the client, its areas/processes and tool connectors.</summary>
[ApiController]
[Route("api/v1/tenants")]
public class TenantsController : ControllerBase
{
    private readonly StarsDbContext _db;
    public TenantsController(StarsDbContext db) => _db = db;

    [HttpPost]
    public async Task<ActionResult<IdResponse>> Create(CreateTenantRequest req, CancellationToken ct)
    {
        var t = new Tenant { Name = req.Name, Slug = req.Slug, Status = TenantStatus.Active };
        _db.Tenants.Add(t);
        await _db.SaveChangesAsync(ct);
        return new IdResponse(t.Id);
    }

    [HttpPost("{tenantId:guid}/business-units")]
    public async Task<ActionResult<IdResponse>> AddBusinessUnit(Guid tenantId, CreateBusinessUnitRequest req, CancellationToken ct)
    {
        var bu = new BusinessUnit
        {
            TenantId = tenantId,
            Name = req.Name,
            Code = req.Code,
            ManagerName = req.ManagerName,
            ManagerEmail = req.ManagerEmail
        };
        _db.BusinessUnits.Add(bu);
        await _db.SaveChangesAsync(ct);
        return new IdResponse(bu.Id);
    }

    [HttpPost("{tenantId:guid}/processes")]
    public async Task<ActionResult<IdResponse>> AddProcess(Guid tenantId, CreateProcessRequest req, CancellationToken ct)
    {
        var p = new BusinessProcess
        {
            TenantId = tenantId,
            Name = req.Name,
            ProcessCategory = req.ProcessCategory,
            Classification = req.Classification,
            ProcessValue = req.ProcessValue
        };
        _db.Processes.Add(p);
        await _db.SaveChangesAsync(ct);
        return new IdResponse(p.Id);
    }

    [HttpPost("{tenantId:guid}/connectors")]
    public async Task<ActionResult<IdResponse>> AddConnector(Guid tenantId, CreateConnectorRequest req, CancellationToken ct)
    {
        var c = new ConnectorConfig
        {
            TenantId = tenantId,
            Provider = req.Provider,
            Capability = req.Capability,
            DisplayName = req.DisplayName,
            AuthType = req.AuthType,
            EncryptedSettings = req.EncryptedSettings, // caller sends already-encrypted blob
            SyncIntervalMinutes = req.SyncIntervalMinutes
        };
        _db.Connectors.Add(c);
        await _db.SaveChangesAsync(ct);
        return new IdResponse(c.Id);
    }
}
