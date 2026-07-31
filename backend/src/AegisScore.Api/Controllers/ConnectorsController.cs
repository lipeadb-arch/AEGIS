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
/// [Alto 3 / Médio 6] O tenant NUNCA vem da rota — o conector é resolvido pelo
/// <see cref="ITenantManagementService"/> DENTRO do tenant do JWT; id de outro cliente e id inexistente
/// devolvem o MESMO 404, sem confirmar existência.
///
/// [AEGIS-AUD-020] A orquestração da COLETA (mapping, proteção, dedupe, persistência, LastSyncAt/LastStatus)
/// deixou de viver aqui: passou para a autoridade ÚNICA <see cref="IEvidenceIngestionExecutor"/>, compartilhada
/// com a ingestão push. Este controller apenas valida o contrato/tenant e delega.
/// </summary>
[ApiController]
[Route("api/v1/connectors")]
[Authorize]
public class ConnectorsController : ControllerBase
{
    private readonly ITenantManagementService _connectors;
    private readonly IConnectorRegistry _registry;
    private readonly IEvidenceIngestionExecutor _executor;

    public ConnectorsController(
        ITenantManagementService connectors, IConnectorRegistry registry, IEvidenceIngestionExecutor executor)
    {
        _connectors = connectors;
        _registry = registry;
        _executor = executor;
    }

    /// <summary>
    /// Lista os conectores DESTE tenant (implícito no JWT) para a tela de integrações. Somente leitura e sem
    /// segredo: só os booleanos <c>hasCredentials</c>/<c>hasIngestionKey</c> atravessam a fronteira.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ConnectorConfigDto>>> List(CancellationToken ct)
    {
        var connectors = await _connectors.ListConnectorsAsync(ct);
        return Ok(connectors
            .Select(c => new ConnectorConfigDto(
                c.ConnectorId, c.Provider.ToString(), c.Capability.ToString(), c.DisplayName,
                c.AuthType.ToString(), c.Enabled, c.SyncIntervalMinutes, c.LastSyncAt,
                c.LastStatus.ToString(), c.HasCredentials, c.HasIngestionKey))
            .ToList());
    }

    [HttpPost("{connectorId:guid}/test")]
    public async Task<ActionResult<ConnectorHealthDto>> Test(Guid connectorId, CancellationToken ct)
    {
        var cfg = await _connectors.GetConnectorAsync(connectorId, ct);
        if (cfg is null) return NotFound();

        // [AEGIS-AUD-020] Conector genérico de PUSH: não há fornecedor externo a contatar. O teste apenas
        // confirma a PRONTIDÃO LOCAL — habilitado + chave de ingestão configurada —, sem inventar uma chamada.
        if (IsGenericPush(cfg))
        {
            var ready = cfg.Enabled && !string.IsNullOrWhiteSpace(cfg.IngestionKeyHash);
            return new ConnectorHealthDto(
                (ready ? ConnectorStatus.Healthy : ConnectorStatus.Degraded).ToString(),
                ready
                    ? "Pronto para receber push (chave de ingestão configurada)."
                    : "Configure uma chave de ingestão e habilite o conector para receber eventos.");
        }

        var connector = _registry.Resolve(cfg.Provider, cfg.Capability);
        if (connector is null)
            return Problem($"No adapter registered for {cfg.Provider}/{cfg.Capability}.", statusCode: 501);

        var health = await connector.TestAsync(cfg, ct);
        return new ConnectorHealthDto(health.Status.ToString(), health.Message);
    }

    /// <summary>
    /// Coleta PULL sob demanda: delega ao executor único (coleta via adaptador → mapping determinístico →
    /// proteção → persistência → carimbo). 404 fora do tenant; 501 quando não há adaptador pull; falha
    /// operacional vira LastStatus=Failed no próprio executor e sobe como 500.
    /// </summary>
    [HttpPost("{connectorId:guid}/sync")]
    public async Task<ActionResult<SyncResultDto>> Sync(Guid connectorId, CancellationToken ct)
    {
        var cfg = await _connectors.GetConnectorAsync(connectorId, ct);
        if (cfg is null) return NotFound();

        var result = await _executor.CollectPullAsync(cfg, ct);
        if (result is null)
            return Problem($"No adapter registered for {cfg.Provider}/{cfg.Capability}.", statusCode: 501);

        // O front usa apenas a contagem; a lista de sinais fica vazia (os sinais são internos, sob outro contexto).
        return new SyncResultDto(result.Persisted, Array.Empty<SignalDto>());
    }

    private static bool IsGenericPush(ConnectorConfig c) =>
        c.Provider == ConnectorProvider.Generic
        && (c.Capability == ConnectorCapability.Siem || c.Capability == ConnectorCapability.Edr);
}
