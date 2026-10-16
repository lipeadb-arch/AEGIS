using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Api.Contracts;
using System.Security.Claims;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
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
    private readonly IKnightSyncRequests _knightSync;

    public ConnectorsController(
        ITenantManagementService connectors,
        IConnectorRegistry registry,
        IKnightSyncRequests knightSync)
    {
        _connectors = connectors;
        _registry = registry;
        _knightSync = knightSync;
    }

    /// <summary>
    /// Lista os conectores DESTE tenant (implícito no JWT) para a tela de integrações. Somente leitura e sem
    /// segredo: só os booleanos <c>hasCredentials</c>/<c>hasIngestionKey</c> atravessam a fronteira.
    /// Para coletas de vulnerabilidade em background, NÃO publica Healthy/LastSyncAt enquanto o executor ainda
    /// está reconciliando/persistindo: a UI recebe status transitório "Syncing" e timestamp nulo até o fim real.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ConnectorConfigDto>>> List(CancellationToken ct)
    {
        var connectors = await _connectors.ListConnectorsAsync(ct);
        // [AEGIS-KNIGHT-MULTICLOUD-01] Pedido DURÁVEL ativo do KNIGHT também é "sincronizando" — lido do banco,
        // e não de memória, para valer entre réplicas e depois de um reinício.
        var knightActive = await _knightSync.ActiveConnectorIdsAsync(ct);
        return Ok(connectors
            .Select(c =>
            {
                var syncing = knightActive.Contains(c.ConnectorId);
                return new ConnectorConfigDto(
                    c.ConnectorId, c.Provider.ToString(), c.Capability.ToString(), c.DisplayName,
                    c.AuthType.ToString(), c.Enabled, c.SyncIntervalMinutes,
                    syncing ? null : c.LastSyncAt,
                    syncing ? "Syncing" : c.LastStatus.ToString(),
                    c.HasCredentials, c.HasIngestionKey);
            })
            .ToList());
    }

    [HttpPost("{connectorId:guid}/test")]
    public async Task<ActionResult<ConnectorHealthDto>> Test(Guid connectorId, CancellationToken ct)
    {
        var cfg = await _connectors.GetConnectorAsync(connectorId, ct);
        if (cfg is null) return NotFound();
        if (!ProductConnectorScope.Supports(cfg.Provider, cfg.Capability))
            return Problem("Integração aposentada. Use as fontes do AEGIS KNIGHT ou a biblioteca do AEGIS NIST.", statusCode: 410);

        // [AEGIS-MVP-ADMIN-LIFECYCLE-01] Conector DESCONECTADO (credencial eliminada) não pode ser testado: sem
        // segredo não há o que autenticar contra o provedor. 409 orientado à ação (reconecte antes). Push tem
        // sua própria leitura de prontidão acima (chave de ingestão), então esta guarda é do caminho pull.
        if (string.IsNullOrWhiteSpace(cfg.EncryptedSettings))
            return Conflict(new
            {
                title = "Conector desconectado: informe a credencial novamente antes de testar.",
                status = 409,
            });

        var connector = _registry.Resolve(cfg.Provider, cfg.Capability);
        if (connector is null)
            return Problem($"No adapter registered for {cfg.Provider}/{cfg.Capability}.", statusCode: 501);

        var health = await connector.TestAsync(cfg, ct);
        return new ConnectorHealthDto(health.Status.ToString(), health.Message);
    }

    /// <summary>
    /// Coleta PULL sob demanda. Coletores rápidos continuam síncronos. VulnerabilityScanner é deliberadamente
    /// destacado da requisição HTTP e responde 202 imediatamente, porque um tenant real pode ter centenas de
    /// milhares de relações machine×CVE e ultrapassar o timeout do gateway do Render.
    /// </summary>
    /// <param name="source">
    /// [AEGIS-KNIGHT-COVERAGE-02] Fonte KNIGHT a sincronizar quando o conector alimenta mais de uma ("entra",
    /// "teams"). Omitida, vale a fonte PADRÃO do conector — o comportamento anterior, preservado. Um nome que o
    /// conector não alimenta é recusado com 400, em vez de sincronizar outra coisa em silêncio.
    /// </param>
    [HttpPost("{connectorId:guid}/sync")]
    public async Task<IActionResult> Sync(Guid connectorId, [FromQuery] string? source, CancellationToken ct)
    {
        var cfg = await _connectors.GetConnectorAsync(connectorId, ct);
        if (cfg is null) return NotFound();
        if (!ProductConnectorScope.Supports(cfg.Provider, cfg.Capability))
            return Problem("Integração aposentada. Use as fontes do AEGIS KNIGHT ou a biblioteca do AEGIS NIST.", statusCode: 410);

        // [AEGIS-MVP-ADMIN-LIFECYCLE-01] Novas coletas são recusadas de forma controlada quando o conector está
        // desabilitado (coleta pausada) ou desconectado (credencial eliminada). 409 orientado à ação. Isto,
        // somado ao registro de sync que NUNCA reescreve a credencial, impede que uma coleta em andamento
        // "ressuscite" uma conexão desconectada no meio do caminho.
        if (!cfg.Enabled)
            return Conflict(new { title = "Conector desabilitado: reative-o antes de sincronizar.", status = 409 });
        if (string.IsNullOrWhiteSpace(cfg.EncryptedSettings) && string.IsNullOrWhiteSpace(cfg.IngestionKeyHash))
            return Conflict(new
            {
                title = "Conector desconectado: informe a credencial novamente antes de sincronizar.",
                status = 409,
            });

        // [AEGIS-KNIGHT-MULTICLOUD-01] Postura de identidade (AEGIS KNIGHT): o pedido é registrado de forma
        // DURÁVEL e identificada, e processado pelo worker com a MESMA autoridade de coleta → ADM → avaliação.
        // 202 imediato com o identificador — a tela acompanha ESTE pedido, sem depender do tempo limite do
        // navegador. Um segundo clique com pedido ativo devolve o MESMO pedido (sem nova coleta).
        //
        // [AEGIS-KNIGHT-COVERAGE-02] O conector Microsoft alimenta DUAS fontes (Entra ID e Teams), cada uma com o
        // próprio pedido e o próprio resultado. Sincronizar uma NÃO invalida nem apaga a avaliação da outra.
        if (KnightConnectorSources.SourcesOf(cfg.Provider, cfg.Capability).Count > 0)
        {
            if (KnightConnectorSources.Resolve(cfg.Provider, cfg.Capability, source) is not { } knightSource)
                return BadRequest(new
                {
                    title = $"Este conector não sincroniza a fonte “{source}”.",
                    status = 400,
                });

            var enqueued = await _knightSync.EnqueueAsync(cfg.Id, knightSource, RequesterId(), ct);
            return Accepted(ToDto(enqueued.Request, enqueued.AlreadyActive));
        }

        return Problem("Esta integração usa a biblioteca de documentos do NIST para sincronizar.", statusCode: 410);
    }

    [HttpGet("{connectorId:guid}/sync-requests/{requestId:guid}")]
    public async Task<ActionResult<KnightSyncRequestDto>> GetSyncRequest(Guid connectorId, Guid requestId, CancellationToken ct)
    {
        var cfg = await _connectors.GetConnectorAsync(connectorId, ct);
        if (cfg is null) return NotFound();
        if (!ProductConnectorScope.Supports(cfg.Provider, cfg.Capability))
            return Problem("Integração aposentada. Use as fontes do AEGIS KNIGHT ou a biblioteca do AEGIS NIST.", statusCode: 410);
        var view = await _knightSync.GetAsync(connectorId, requestId, ct);
        return view is null ? NotFound() : Ok(ToDto(view, false));
    }

    /// <summary>
    /// [AEGIS-KNIGHT-MULTICLOUD-01] O pedido mais recente do conector (204 quando nunca houve).
    /// [AEGIS-KNIGHT-COVERAGE-02] Com <c>?source=</c>, o mais recente DAQUELA fonte — necessário porque um
    /// conector pode alimentar mais de uma, e cada linha da tela acompanha a sua.
    /// </summary>
    [HttpGet("{connectorId:guid}/sync-requests/latest")]
    public async Task<ActionResult<KnightSyncRequestDto>> GetLatestSyncRequest(
        Guid connectorId, [FromQuery] string? source, CancellationToken ct)
    {
        var cfg = await _connectors.GetConnectorAsync(connectorId, ct);
        if (cfg is null) return NotFound();
        if (!ProductConnectorScope.Supports(cfg.Provider, cfg.Capability))
            return Problem("Integração aposentada. Use as fontes do AEGIS KNIGHT ou a biblioteca do AEGIS NIST.", statusCode: 410);

        KnightSourceType? wanted = null;
        if (!string.IsNullOrWhiteSpace(source))
        {
            wanted = KnightConnectorSources.Resolve(cfg.Provider, cfg.Capability, source);
            if (wanted is null) return BadRequest(new { title = $"Este conector não sincroniza a fonte “{source}”.", status = 400 });
        }

        var view = await _knightSync.GetLatestAsync(connectorId, wanted, ct);
        return view is null ? NoContent() : Ok(ToDto(view, false));
    }

    private Guid? RequesterId()
    {
        var raw = User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static KnightSyncRequestDto ToDto(KnightSyncRequestView v, bool alreadyActive) => new(
        v.Id, v.ConnectorId, v.Source.ToString(), v.Status.ToString(), v.RequestedAt, v.StartedAt, v.CompletedAt,
        v.Attempts, v.RunId, v.ResultSourceState?.ToString(), v.FailureCategory, v.Message,
        v.LastCompletedRunId, v.LastCompletedAt, alreadyActive);

    // ---- [AEGIS-MVP-ADMIN-LIFECYCLE-01] Ciclo de vida administrativo (TenantAdmin) --------------------
    // Listar/testar/sincronizar são operações de qualquer autenticado; EDITAR o estado de uma integração é
    // ato de administrador do ambiente. A autorização vive no backend (papel do JWT), não só na visibilidade
    // da UI. O tenant nunca vem da rota — o serviço resolve o conector DENTRO do tenant do JWT.

    /// <summary>Edita o nome de exibição e o intervalo de coleta. NUNCA toca a credencial (não há campo de segredo).</summary>
    [HttpPut("{connectorId:guid}")]
    [Authorize(Roles = "TenantAdmin")]
    public async Task<ActionResult<ConnectorConfigDto>> Update(
        Guid connectorId, UpdateConnectorRequest req, CancellationToken ct)
    {
        var result = await _connectors.UpdateConnectorAsync(
            new UpdateConnectorCommand(connectorId, req.DisplayName, req.SyncIntervalMinutes), ct);
        return RespondAdmin(result);
    }

    /// <summary>Desabilita (pausa novas coletas; PRESERVA a credencial para futura reativação). Idempotente.</summary>
    [HttpPost("{connectorId:guid}/disable")]
    [Authorize(Roles = "TenantAdmin")]
    public async Task<ActionResult<ConnectorConfigDto>> Disable(Guid connectorId, CancellationToken ct) =>
        RespondAdmin(await _connectors.SetConnectorEnabledAsync(connectorId, enabled: false, ct));

    /// <summary>Reabilita um conector desabilitado (retoma coletas com a credencial preservada). Idempotente.</summary>
    [HttpPost("{connectorId:guid}/enable")]
    [Authorize(Roles = "TenantAdmin")]
    public async Task<ActionResult<ConnectorConfigDto>> Enable(Guid connectorId, CancellationToken ct) =>
        RespondAdmin(await _connectors.SetConnectorEnabledAsync(connectorId, enabled: true, ct));

    /// <summary>
    /// Desconecta: desabilita E elimina TODO o material secreto (EncryptedSettings + IngestionKeyHash),
    /// preservando a linha e a proveniência histórica. Reconfigurar depois reativa a MESMA linha (upsert pela
    /// chave natural). Idempotente.
    /// </summary>
    [HttpPost("{connectorId:guid}/disconnect")]
    [Authorize(Roles = "TenantAdmin")]
    public async Task<ActionResult<ConnectorConfigDto>> Disconnect(Guid connectorId, CancellationToken ct) =>
        RespondAdmin(await _connectors.DisconnectConnectorAsync(connectorId, ct));

    /// <summary>Traduz o desfecho administrativo em HTTP: 200 (aplicado) · 404 (inexistente/cross-tenant) · 400 (nome) · 409 (habilitar desconectado).</summary>
    private ActionResult<ConnectorConfigDto> RespondAdmin(ConnectorAdminResult result) => result.Status switch
    {
        ConnectorAdminStatus.Updated => Ok(ToDto(result.Connector!)),
        ConnectorAdminStatus.NotFound => NotFound(),
        ConnectorAdminStatus.InvalidDisplayName =>
            BadRequest(new { title = result.Detail ?? "Nome de exibição inválido.", status = 400 }),
        // Habilitar um conector desconectado é conflito de ESTADO, não requisição malformada: 409 orientado à
        // ação (reconecte antes). A resposta carrega só {title, status} — nunca segredo.
        ConnectorAdminStatus.MissingCredential =>
            Conflict(new { title = result.Detail ?? "Conector desconectado: reconecte antes de habilitar.", status = 409 }),
        _ => BadRequest(new { title = "Requisição inválida.", status = 400 }),
    };

    /// <summary>Projeção SEGURA (sem segredo) do resumo de conector para o DTO da API — enums como STRING.</summary>
    private static ConnectorConfigDto ToDto(ConnectorSummary c) => new(
        c.ConnectorId, c.Provider.ToString(), c.Capability.ToString(), c.DisplayName, c.AuthType.ToString(),
        c.Enabled, c.SyncIntervalMinutes, c.LastSyncAt, c.LastStatus.ToString(), c.HasCredentials, c.HasIngestionKey);

    private static bool IsGenericPush(ConnectorConfig c) =>
        c.Provider == ConnectorProvider.Generic
        && (c.Capability == ConnectorCapability.Siem || c.Capability == ConnectorCapability.Edr);
}

public sealed record SyncAcceptedDto(bool Queued, string Message);

/// <summary>
/// [AEGIS-KNIGHT-MULTICLOUD-01] Pedido de sincronização do KNIGHT. <c>RunId</c> só existe quando concluído;
/// <c>LastCompletedRunId</c> é a última avaliação concluída da fonte — o que continua valendo se este falhar.
/// </summary>
public sealed record KnightSyncRequestDto(
    Guid Id, Guid ConnectorId, string Source, string Status, DateTimeOffset RequestedAt, DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt, int Attempts, Guid? RunId, string? ResultSourceState, string? FailureCategory,
    string? Message, Guid? LastCompletedRunId, DateTimeOffset? LastCompletedAt, bool AlreadyActive);
