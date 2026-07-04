using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Api.Controllers;

/// <summary>Operate connectors: health check and on-demand collection (Facade in action).</summary>
[ApiController]
[Route("api/v1/tenants/{tenantId:guid}/connectors/{connectorId:guid}")]
public class ConnectorsController : ControllerBase
{
    private readonly AegisScoreDbContext _db;
    private readonly IConnectorRegistry _registry;

    public ConnectorsController(AegisScoreDbContext db, IConnectorRegistry registry)
    {
        _db = db;
        _registry = registry;
    }

    [HttpPost("test")]
    public async Task<ActionResult<ConnectorHealthDto>> Test(Guid tenantId, Guid connectorId, CancellationToken ct)
    {
        var cfg = await _db.Connectors.FirstOrDefaultAsync(c => c.Id == connectorId && c.TenantId == tenantId, ct);
        if (cfg is null) return NotFound();

        var connector = _registry.Resolve(cfg.Provider, cfg.Capability);
        if (connector is null)
            return Problem($"No adapter registered for {cfg.Provider}/{cfg.Capability}.", statusCode: 501);

        var health = await connector.TestAsync(cfg, ct);
        return new ConnectorHealthDto(health.Status.ToString(), health.Message);
    }

    [HttpPost("sync")]
    public async Task<ActionResult<SyncResultDto>> Sync(Guid tenantId, Guid connectorId, CancellationToken ct)
    {
        var cfg = await _db.Connectors.FirstOrDefaultAsync(c => c.Id == connectorId && c.TenantId == tenantId, ct);
        if (cfg is null) return NotFound();

        var connector = _registry.Resolve(cfg.Provider, cfg.Capability);
        if (connector is null)
            return Problem($"No adapter registered for {cfg.Provider}/{cfg.Capability}.", statusCode: 501);

        var collected = new List<EvidenceSignal>();
        await foreach (var signal in connector.CollectAsync(cfg, ct))
        {
            collected.Add(signal);
            _db.Signals.Add(signal);
        }

        cfg.LastSyncAt = DateTimeOffset.UtcNow;
        cfg.LastStatus = ConnectorStatus.Healthy;
        await _db.SaveChangesAsync(ct);

        var dtos = collected
            .Select(s => new SignalDto(s.SignalKey, s.NumericValue, s.Unit, s.Severity, s.MappedSubcategoryCodes, s.CollectedAt))
            .ToList();
        return new SyncResultDto(dtos.Count, dtos);
    }
}
