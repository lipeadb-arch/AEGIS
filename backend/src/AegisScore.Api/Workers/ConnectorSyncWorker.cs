using System.Collections.Concurrent;
using System.Threading.Channels;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;

namespace AegisScore.Api.Workers;

/// <summary>
/// Fila em memória para sincronizações PULL longas disparadas manualmente pela API.
/// A fila recebe o ConnectorConfig já resolvido dentro do tenant autenticado; EncryptedSettings permanece cifrado.
/// Um mesmo conector não pode ficar simultaneamente enfileirado/em execução duas vezes.
/// </summary>
public interface IConnectorSyncQueue
{
    bool TryEnqueue(ConnectorConfig config);
    bool IsPending(Guid connectorId);
    ValueTask<ConnectorConfig> DequeueAsync(CancellationToken ct);
    void Complete(Guid connectorId);
}

public sealed class ConnectorSyncQueue : IConnectorSyncQueue
{
    private const int Capacity = 16;
    private readonly Channel<ConnectorConfig> _channel = Channel.CreateBounded<ConnectorConfig>(new BoundedChannelOptions(Capacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite,
    });
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();

    public bool TryEnqueue(ConnectorConfig config)
    {
        if (!_pending.TryAdd(config.Id, 0)) return false;
        if (_channel.Writer.TryWrite(config)) return true;
        _pending.TryRemove(config.Id, out _);
        return false;
    }

    public bool IsPending(Guid connectorId) => _pending.ContainsKey(connectorId);

    public ValueTask<ConnectorConfig> DequeueAsync(CancellationToken ct) => _channel.Reader.ReadAsync(ct);

    public void Complete(Guid connectorId) => _pending.TryRemove(connectorId, out _);
}

/// <summary>
/// Executa sincronizações longas fora da requisição HTTP. Isso evita manter a conexão do browser/Render aberta
/// por muitos minutos durante tenants grandes (por exemplo centenas de milhares de relações machine×CVE).
/// O executor continua sendo a autoridade única de coleta/reconciliação e atualiza LastStatus/LastSyncAt.
/// </summary>
public sealed class ConnectorSyncWorker : BackgroundService
{
    private readonly IConnectorSyncQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ConnectorSyncWorker> _log;

    public ConnectorSyncWorker(
        IConnectorSyncQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<ConnectorSyncWorker> log)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ConnectorConfig config;
            try
            {
                config = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                _log.LogInformation(
                    "Sincronização em segundo plano iniciada para conector {ConnectorId} ({Provider}/{Capability}).",
                    config.Id, config.Provider, config.Capability);

                using var scope = _scopeFactory.CreateScope();
                var executor = scope.ServiceProvider.GetRequiredService<IEvidenceIngestionExecutor>();
                var result = await executor.CollectPullAsync(config, stoppingToken);

                if (result is null)
                {
                    _log.LogWarning(
                        "Sincronização em segundo plano sem adaptador para conector {ConnectorId}.", config.Id);
                    continue;
                }

                _log.LogInformation(
                    "Sincronização em segundo plano concluída para conector {ConnectorId}. Sinais persistidos: {Persisted}; vulnerabilidades: {HasVulnerabilities}.",
                    config.Id, result.Persisted, result.Vulnerabilities is not null);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _log.LogInformation("Worker de sincronização interrompido durante shutdown.");
                break;
            }
            catch (Exception ex)
            {
                // O EvidenceIngestionExecutor já carimba LastStatus=Failed e registra o stack trace técnico.
                // Aqui não propagamos a exceção para não derrubar o hosted service nem o processo web.
                _log.LogError(ex, "Sincronização em segundo plano falhou para conector {ConnectorId}.", config.Id);
            }
            finally
            {
                _queue.Complete(config.Id);
            }
        }
    }
}
