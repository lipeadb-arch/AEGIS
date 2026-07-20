using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Domain;

namespace AegisScore.Api.Controllers;

/// <summary>
/// Operate connectors: health check and on-demand collection (Facade in action).
///
/// [Alto 3 / Médio 6] O tenant NUNCA vem da rota — mesmo tratamento já aplicado ao
/// <see cref="TenantsController"/>. A rota antiga era <c>tenants/{tenantId}/connectors/{connectorId}</c>.
///
/// ⚠️ Precisão sobre o risco: o endpoint NÃO era anônimo — a <c>FallbackPolicy</c> de Program.cs já
/// exige usuário autenticado em tudo que não seja <c>[AllowAnonymous]</c>, e o Global Query Filter
/// fechava o vazamento cross-tenant. O que se corrige aqui é (a) a dependência de um default GLOBAL:
/// sem <c>[Authorize]</c> local, mexer na FallbackPolicy abriria esta rota em silêncio; e (b) um
/// <c>tenantId</c> de rota que PARECIA governar autorização sem governar nada — forma clássica de IDOR
/// latente, que convida a próxima pessoa a confiar no parâmetro.
///
/// Agora o conector é resolvido pelo <see cref="ITenantManagementService"/> DENTRO do tenant do JWT —
/// id de outro cliente e id inexistente devolvem o MESMO 404, sem confirmar existência.
/// </summary>
[ApiController]
[Route("api/v1/connectors")]
[Authorize]
public class ConnectorsController : ControllerBase
{
    private readonly ITenantManagementService _connectors;
    private readonly IConnectorRegistry _registry;

    public ConnectorsController(ITenantManagementService connectors, IConnectorRegistry registry)
    {
        _connectors = connectors;
        _registry = registry;
    }

    /// <summary>
    /// Lista os conectores DESTE tenant (implícito no JWT) para a tela de integrações. Somente leitura
    /// e sem segredo: só o booleano <c>hasCredentials</c> atravessa a fronteira.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ConnectorConfigDto>>> List(CancellationToken ct)
    {
        var connectors = await _connectors.ListConnectorsAsync(ct);
        return Ok(connectors
            .Select(c => new ConnectorConfigDto(
                c.ConnectorId, c.Provider.ToString(), c.Capability.ToString(), c.DisplayName,
                c.AuthType.ToString(), c.Enabled, c.SyncIntervalMinutes, c.LastSyncAt,
                c.LastStatus.ToString(), c.HasCredentials))
            .ToList());
    }

    [HttpPost("{connectorId:guid}/test")]
    public async Task<ActionResult<ConnectorHealthDto>> Test(Guid connectorId, CancellationToken ct)
    {
        var cfg = await _connectors.GetConnectorAsync(connectorId, ct);
        if (cfg is null) return NotFound();

        var connector = _registry.Resolve(cfg.Provider, cfg.Capability);
        if (connector is null)
            return Problem($"No adapter registered for {cfg.Provider}/{cfg.Capability}.", statusCode: 501);

        var health = await connector.TestAsync(cfg, ct);
        return new ConnectorHealthDto(health.Status.ToString(), health.Message);
    }

    [HttpPost("{connectorId:guid}/sync")]
    public async Task<ActionResult<SyncResultDto>> Sync(Guid connectorId, CancellationToken ct)
    {
        var cfg = await _connectors.GetConnectorAsync(connectorId, ct);
        if (cfg is null) return NotFound();

        var connector = _registry.Resolve(cfg.Provider, cfg.Capability);
        if (connector is null)
            return Problem($"No adapter registered for {cfg.Provider}/{cfg.Capability}.", statusCode: 501);

        var collected = new List<EvidenceSignal>();
        try
        {
            await foreach (var signal in connector.CollectAsync(cfg, ct))
                collected.Add(signal);
        }
        catch (Exception)
        {
            // A coleta falhou no meio: registra o DESFECHO (LastStatus = Failed) para que a saúde do
            // conector conte a verdade, e relança — o GlobalExceptionHandlingMiddleware é quem loga e
            // responde, sem vazar internals. Sem isto, LastStatus ficaria eternamente "Healthy".
            await _connectors.RecordSyncResultAsync(
                connectorId, Array.Empty<EvidenceSignal>(), ConnectorStatus.Failed, CancellationToken.None);
            throw;
        }

        // Sinais + carimbo de sync numa única transação, dentro do tenant ambiente.
        await _connectors.RecordSyncResultAsync(connectorId, collected, ConnectorStatus.Healthy, ct);

        var dtos = collected
            .Select(s => new SignalDto(s.SignalKey, s.NumericValue, s.Unit, s.Severity, s.MappedSubcategoryCodes, s.CollectedAt))
            .ToList();
        return new SyncResultDto(dtos.Count, dtos);
    }
}
