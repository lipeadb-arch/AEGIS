using System.Collections.Concurrent;
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
    // Sincronizações de VulnerabilityScanner podem levar muitos minutos em tenants grandes. Mantê-las presas à
    // requisição HTTP faz o proxy da hospedagem expirar (502/504) mesmo quando o Defender continua respondendo.
    // O dicionário evita duas execuções simultâneas do MESMO conector dentro desta instância e também é a fonte
    // transitória do estado "Syncing" exposto pela listagem enquanto a reconciliação ainda não terminou.
    private static readonly ConcurrentDictionary<Guid, byte> BackgroundSyncs = new();

    private readonly ITenantManagementService _connectors;
    private readonly IConnectorRegistry _registry;
    private readonly IEvidenceIngestionExecutor _executor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ConnectorsController> _log;

    public ConnectorsController(
        ITenantManagementService connectors,
        IConnectorRegistry registry,
        IEvidenceIngestionExecutor executor,
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime lifetime,
        ILogger<ConnectorsController> log)
    {
        _connectors = connectors;
        _registry = registry;
        _executor = executor;
        _scopeFactory = scopeFactory;
        _lifetime = lifetime;
        _log = log;
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
        return Ok(connectors
            .Select(c =>
            {
                var syncing = BackgroundSyncs.ContainsKey(c.ConnectorId);
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
    /// Coleta PULL sob demanda. Coletores rápidos continuam síncronos. VulnerabilityScanner é deliberadamente
    /// destacado da requisição HTTP e responde 202 imediatamente, porque um tenant real pode ter centenas de
    /// milhares de relações machine×CVE e ultrapassar o timeout do gateway do Render.
    /// </summary>
    [HttpPost("{connectorId:guid}/sync")]
    public async Task<IActionResult> Sync(Guid connectorId, CancellationToken ct)
    {
        var cfg = await _connectors.GetConnectorAsync(connectorId, ct);
        if (cfg is null) return NotFound();

        if (cfg.Capability == ConnectorCapability.VulnerabilityScanner)
        {
            if (!BackgroundSyncs.TryAdd(cfg.Id, 0))
                return Accepted(new SyncAcceptedDto(true, "Sincronização de vulnerabilidades já está em andamento."));

            // O ConnectorConfig contém apenas o blob EncryptedSettings; nenhum segredo em claro é capturado.
            // O trabalho usa escopo próprio e ApplicationStopping, nunca RequestAborted: o gateway/browser pode
            // encerrar a requisição sem cancelar a coleta longa.
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var executor = scope.ServiceProvider.GetRequiredService<IEvidenceIngestionExecutor>();
                    _log.LogInformation(
                        "Sincronização de vulnerabilidades em segundo plano iniciada para conector {ConnectorId}.",
                        cfg.Id);

                    var result = await executor.CollectPullAsync(cfg, _lifetime.ApplicationStopping);
                    _log.LogInformation(
                        "Sincronização de vulnerabilidades em segundo plano concluída para conector {ConnectorId}; resultado presente: {HasResult}.",
                        cfg.Id, result is not null);
                }
                catch (OperationCanceledException) when (_lifetime.ApplicationStopping.IsCancellationRequested)
                {
                    _log.LogInformation(
                        "Sincronização de vulnerabilidades {ConnectorId} interrompida por shutdown da aplicação.", cfg.Id);
                }
                catch (Exception ex)
                {
                    // O executor é a autoridade que carimba Failed. Não devolvemos body/HTML/stack trace ao browser.
                    _log.LogError(ex,
                        "Sincronização de vulnerabilidades em segundo plano falhou para conector {ConnectorId}.", cfg.Id);
                }
                finally
                {
                    // Só removemos o estado transitório depois de CollectPullAsync terminar por sucesso/falha.
                    // Portanto, a listagem não consegue mostrar "Operacional" enquanto a reconciliação está rodando.
                    BackgroundSyncs.TryRemove(cfg.Id, out _);
                }
            }, CancellationToken.None);

            return Accepted(new SyncAcceptedDto(true,
                "Sincronização de vulnerabilidades iniciada em segundo plano. O status será atualizado ao concluir."));
        }

        var result = await _executor.CollectPullAsync(cfg, ct);
        if (result is null)
            return Problem($"No adapter registered for {cfg.Provider}/{cfg.Capability}.", statusCode: 501);

        // [AEGIS-MVP-VULN-01] Um conector de vulnerabilidade não produz sinais (SignalsCollected=0), mas devolve as
        // contagens de ativos/CVEs/exposições/observações — o usuário nunca vê "0 coletados" após um sync real.
        var vuln = result.Vulnerabilities is { } v
            ? new VulnerabilitySyncSummaryDto(
                v.MachinesObserved, v.AssetsCreated, v.CvesUpserted, v.ExposuresCreated,
                v.ObservationsOpened, v.ObservationsReopened, v.ObservationsResolved,
                v.BindingsDeactivated, v.AssetsDeactivated, v.WasComplete,
                v.InvalidMachines, v.InvalidCves, v.InvalidRelations)
            : null;

        // [AEGIS-MVP-SIEM] Fotografia operacional PROVIDER-NEUTRAL do SIEM (fato consultivo; nunca sinal/score). O
        // enum de estado/período vai como STRING (mesmo idioma dos demais DTOs de leitura); contagens seguem anuláveis.
        var siem = result.Siem is { } s
            ? new SiemSyncSummaryDto(
                s.Source, s.IsComplete,
                new SiemCasePostureDto(
                    s.Cases.State.ToString(), s.Cases.Period.ToString(), s.Cases.WindowDays, s.Cases.IsComplete,
                    s.Cases.Observed, s.Cases.Open, s.Cases.New, s.Cases.Closed,
                    s.Cases.OpenHighSeverity, s.Cases.OpenMediumSeverity, s.Cases.OpenLowSeverity,
                    s.Cases.OpenInformationalSeverity,
                    s.Cases.OpenByPriority is { } bp
                        ? bp.Select(p => new SiemPriorityCountDto(p.Priority, p.Count)).ToList()
                        : null,
                    s.Cases.MeanTimeToCloseHours, s.Cases.LastEvidenceAt),
                new SiemAlertPostureDto(
                    s.Alerts.State.ToString(), s.Alerts.Period.ToString(), s.Alerts.WindowDays, s.Alerts.IsComplete,
                    s.Alerts.Observed, s.Alerts.HighSeverity, s.Alerts.MediumSeverity, s.Alerts.LastEvidenceAt))
            : null;

        return Ok(new SyncResultDto(result.Persisted, Array.Empty<SignalDto>(), vuln, siem));
    }

    private static bool IsGenericPush(ConnectorConfig c) =>
        c.Provider == ConnectorProvider.Generic
        && (c.Capability == ConnectorCapability.Siem || c.Capability == ConnectorCapability.Edr);
}

public sealed record SyncAcceptedDto(bool Queued, string Message);
