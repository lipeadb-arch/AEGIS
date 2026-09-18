using Microsoft.Extensions.Options;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Ai;

namespace AegisScore.Api.Workers;

/// <summary>
/// [AEGIS-KNIGHT-MULTICLOUD-01] Processa as sincronizações do KNIGHT solicitadas em Configurações → Integrações.
///
/// Por que desacoplado da requisição: a coleta real de um diretório pode levar minutos e não pode depender do
/// tempo limite do navegador ou do proxy. Por que NÃO é uma tarefa solta: cada pedido é uma linha DURÁVEL
/// adquirida por lease (sobrevive a reinício, não corre em dobro entre réplicas) e é processado num escopo de
/// DI próprio — o DbContext nasce e morre com o pedido, nunca sobrevive ao escopo que o criou.
///
/// O processamento é a MESMA autoridade do caminho HTTP (<see cref="IAegisKnightAssessmentService"/>): uma
/// coleta, uma aquisição do ADM, a avaliação determinística concluída e gravada antes da IA. Falhas não apagam
/// nada — o último snapshot válido e a última avaliação concluída permanecem como estavam.
/// </summary>
public sealed class KnightSyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IKnightSyncQueue _queue;
    private readonly KnightSyncOptions _options;
    private readonly ILogger<KnightSyncWorker> _log;

    public KnightSyncWorker(
        IServiceScopeFactory scopes, IKnightSyncQueue queue, IOptions<KnightSyncOptions> options, ILogger<KnightSyncWorker> log)
    {
        _scopes = scopes;
        _queue = queue;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("Sincronização do KNIGHT desabilitada por configuração (KnightSync:Enabled=false).");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            KnightSyncLease? lease = null;
            try
            {
                lease = await _queue.TryClaimNextAsync(stoppingToken);
                if (lease is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), stoppingToken);
                    continue;
                }

                await ProcessAsync(lease, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Falha da própria fila (banco indisponível): espera e tenta de novo; o pedido, se adquirido,
                // volta a ser elegível quando o lease vencer.
                _log.LogWarning(ex, "Falha no ciclo da fila de sincronização do KNIGHT.");
                try { await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Processa UM pedido: tenta, renova o lease enquanto trabalha e registra o desfecho.</summary>
    internal async Task ProcessAsync(KnightSyncLease lease, CancellationToken stoppingToken)
    {
        if (lease.Attempts > _options.MaxAttempts)
        {
            await _queue.FailAsync(lease.RequestId, lease.LeaseId, "Interrupted",
                "A sincronização foi interrompida antes de concluir (reinício do serviço durante a coleta) e não foi "
                + "refeita de novo. Nenhuma avaliação nova foi registrada; os dados anteriores permanecem.", CancellationToken.None);
            return;
        }

        using var work = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = HeartbeatAsync(lease, work);

        try
        {
            using var scope = _scopes.CreateScope();
            var sp = scope.ServiceProvider;
            // Tenant DONO do pedido, fixado ANTES de resolver qualquer serviço que dependa dele (o DbContext
            // lê o tenant ao ser construído). É o mesmo pipeline do caminho HTTP, sob o tenant certo.
            sp.GetRequiredService<TenantScopeOverride>().Set(lease.TenantId);
            sp.GetService<IAiTenantResolver>()?.OverrideTenant(lease.TenantId);

            var service = sp.GetRequiredService<IAegisKnightAssessmentService>();
            var assessment = await service.RunAssessmentAsync(lease.Source, work.Token);

            var message = assessment.SourceState switch
            {
                KnightSourceState.Completed => "Coleta concluída e avaliação registrada.",
                KnightSourceState.PartialCollection => "Coleta parcial: parte das capacidades não pôde ser lida. A avaliação foi registrada com as limitações declaradas.",
                _ => "A fonte não entregou dados nesta tentativa. A avaliação registra o estado real da coleta, sem substituir dados.",
            };
            await _queue.CompleteAsync(lease.RequestId, lease.LeaseId, assessment.Id, assessment.SourceState, message, CancellationToken.None);
        }
        catch (KnightSourceNotConfiguredException)
        {
            await _queue.FailAsync(lease.RequestId, lease.LeaseId, "ConnectorNotConfigured",
                "O conector está desabilitado, desconectado ou sem credencial válida. Nenhuma coleta foi feita.",
                CancellationToken.None);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Encerramento da aplicação: devolve o pedido sem consumir tentativa.
            await _queue.ReleaseAsync(lease.RequestId, lease.LeaseId, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // O lease foi perdido (outro processo o assumiu): não há desfecho a gravar por aqui.
            _log.LogWarning("Lease da sincronização {Request} do KNIGHT perdido; o processamento foi interrompido.", lease.RequestId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sincronização {Request} do KNIGHT falhou.", lease.RequestId);
            await _queue.FailAsync(lease.RequestId, lease.LeaseId, "CollectionError",
                "A coleta ou a avaliação falhou antes de concluir. Nenhuma avaliação nova foi registrada; os dados anteriores permanecem.",
                CancellationToken.None);
        }
        finally
        {
            work.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>Renova o lease enquanto o pedido é processado; perder o lease cancela o trabalho.</summary>
    private async Task HeartbeatAsync(KnightSyncLease lease, CancellationTokenSource work)
    {
        while (!work.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.HeartbeatSeconds), work.Token);
            bool renewed;
            try { renewed = await _queue.RenewAsync(lease.RequestId, lease.LeaseId, work.Token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Falha ao renovar o lease da sincronização {Request}.", lease.RequestId);
                continue;
            }
            if (!renewed)
            {
                work.Cancel();
                return;
            }
        }
    }
}
