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
///
/// Vínculo durável entre pedido e resultado: a avaliação é gravada JUNTO com o <c>RunId</c> do pedido, numa
/// transação guardada pelo lease. Daí as três garantias de retomada: (1) um pedido que já tem avaliação é
/// finalizado com ELA, sem coletar de novo — depois de queda do processo, de falha ao finalizar ou de outro
/// worker assumir; (2) quem perdeu o lease não grava resultado; (3) um pedido com avaliação nunca é marcado
/// como falho com "nenhuma avaliação nova foi registrada".
/// </summary>
public sealed class KnightSyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IKnightSyncQueue _queue;
    private readonly KnightSyncOptions _options;
    private readonly ILogger<KnightSyncWorker> _log;
    private readonly TimeProvider _clock;

    public KnightSyncWorker(
        IServiceScopeFactory scopes, IKnightSyncQueue queue, IOptions<KnightSyncOptions> options, ILogger<KnightSyncWorker> log,
        TimeProvider? clock = null)
    {
        _scopes = scopes;
        _queue = queue;
        _options = options.Value;
        _log = log;
        _clock = clock ?? TimeProvider.System;
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
        // Retomada: uma tentativa anterior gravou a avaliação deste pedido e não chegou a finalizá-lo. O
        // resultado é ESSE — nada é coletado de novo, e o limite de tentativas não o transforma em falha.
        if (await _queue.GetLinkedResultAsync(lease.RequestId, stoppingToken) is { } alreadyLinked)
        {
            await FinalizeAsync(lease, alreadyLinked.RunId, alreadyLinked.SourceState, recovered: true);
            return;
        }

        if (lease.Attempts > _options.MaxAttempts)
        {
            await _queue.FailAsync(lease.RequestId, lease.LeaseId, "Interrupted",
                "A sincronização foi interrompida antes de concluir (reinício do serviço durante a coleta) e não foi "
                + "refeita de novo. Nenhuma avaliação nova foi registrada; os dados anteriores permanecem.", CancellationToken.None);
            return;
        }

        using var work = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = HeartbeatAsync(lease, work);
        KnightSyncRunResult? outcome = null;

        try
        {
            using var scope = _scopes.CreateScope();
            var sp = scope.ServiceProvider;
            // Tenant DONO do pedido, fixado ANTES de resolver qualquer serviço que dependa dele (o DbContext
            // lê o tenant ao ser construído). É o mesmo pipeline do caminho HTTP, sob o tenant certo.
            sp.GetRequiredService<TenantScopeOverride>().Set(lease.TenantId);
            sp.GetService<IAiTenantResolver>()?.OverrideTenant(lease.TenantId);

            var service = sp.GetRequiredService<IAegisKnightAssessmentService>();
            outcome = await service.RunForSyncRequestAsync(
                lease.Source, new KnightSyncBinding(lease.RequestId, lease.LeaseId), work.Token);
        }
        catch (KnightSourceNotConfiguredException)
        {
            await FailOrRecoverAsync(lease, "ConnectorNotConfigured",
                "O conector está desabilitado, desconectado ou sem credencial válida. Nenhuma coleta foi feita.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Encerramento da aplicação: devolve o pedido sem consumir tentativa. Se a avaliação já tiver sido
            // vinculada, a próxima aquisição o finaliza pela retomada.
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
            await FailOrRecoverAsync(lease, "CollectionError",
                "A coleta ou a avaliação falhou antes de concluir. Nenhuma avaliação nova foi registrada; os dados anteriores permanecem.");
        }
        finally
        {
            work.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
        }

        switch (outcome)
        {
            case { Outcome: KnightSyncRunOutcome.LeaseLost }:
                _log.LogWarning(
                    "Sincronização {Request} do KNIGHT: o lease não pertence mais a esta tentativa; nenhuma avaliação foi gravada por ela.",
                    lease.RequestId);
                break;
            case { Assessment: { } assessment }:
                await FinalizeAsync(lease, assessment.Id, assessment.SourceState,
                    recovered: outcome.Outcome == KnightSyncRunOutcome.AlreadyRegistered);
                break;
            case not null:
                // Vínculo existe, mas a execução não pôde ser lida agora: a retomada lê o vínculo e finaliza.
                _log.LogWarning("Sincronização {Request} do KNIGHT: avaliação vinculada não pôde ser lida; a finalização fica para a retomada.", lease.RequestId);
                break;
        }
    }

    private static string MessageFor(KnightSourceState state) => state switch
    {
        KnightSourceState.Completed => "Coleta concluída e avaliação registrada.",
        KnightSourceState.PartialCollection => "Coleta parcial: parte das capacidades não pôde ser lida. A avaliação foi registrada com as limitações declaradas.",
        _ => "A fonte não entregou dados nesta tentativa. A avaliação registra o estado real da coleta, sem substituir dados.",
    };

    /// <summary>
    /// Finaliza o pedido com a avaliação JÁ vinculada a ele. Se a finalização falhar, nada se perde e nada é
    /// afirmado em falso: o vínculo está gravado, e a próxima aquisição do pedido (quando o lease vencer)
    /// finaliza pela retomada, sem coletar de novo.
    /// </summary>
    private async Task FinalizeAsync(KnightSyncLease lease, Guid runId, KnightSourceState state, bool recovered)
    {
        var message = MessageFor(state) + (recovered
            ? " A avaliação foi gravada por uma tentativa anterior deste mesmo pedido; a coleta não foi repetida."
            : "");
        try
        {
            if (!await _queue.CompleteAsync(lease.RequestId, lease.LeaseId, runId, state, message, CancellationToken.None))
                _log.LogInformation(
                    "Sincronização {Request} do KNIGHT já finalizada por outra tentativa (ou lease perdido); a avaliação {Run} continua vinculada ao pedido.",
                    lease.RequestId, runId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Não foi possível finalizar a sincronização {Request} do KNIGHT. A avaliação {Run} já está gravada e vinculada ao pedido; a retomada a finaliza sem coletar de novo.",
                lease.RequestId, runId);
        }
    }

    /// <summary>
    /// Registra a falha — que a fila recusa se o pedido já tiver avaliação vinculada. Nesse caso o desfecho
    /// verdadeiro é a conclusão com aquela avaliação, e é ela que se grava.
    /// </summary>
    private async Task FailOrRecoverAsync(KnightSyncLease lease, string category, string message)
    {
        try
        {
            if (await _queue.FailAsync(lease.RequestId, lease.LeaseId, category, message, CancellationToken.None)) return;
            if (await _queue.GetLinkedResultAsync(lease.RequestId, CancellationToken.None) is { } linked)
                await FinalizeAsync(lease, linked.RunId, linked.SourceState, recovered: true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Não foi possível registrar o desfecho da sincronização {Request} do KNIGHT; o lease vencerá e o pedido será retomado.", lease.RequestId);
        }
    }

    /// <summary>
    /// Renova o lease enquanto o pedido é processado; perder o lease cancela o trabalho. Se as renovações
    /// falharem por tempo suficiente para o lease vencer, outro worker pode ter assumido o pedido: o trabalho
    /// para aqui — a gravação guardada pelo lease descartaria o resultado de qualquer forma.
    /// </summary>
    private async Task HeartbeatAsync(KnightSyncLease lease, CancellationTokenSource work)
    {
        var lastRenewed = _clock.GetTimestamp();
        while (!work.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.HeartbeatSeconds), _clock, work.Token);
            bool renewed;
            try { renewed = await _queue.RenewAsync(lease.RequestId, lease.LeaseId, work.Token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (_clock.GetElapsedTime(lastRenewed) >= TimeSpan.FromSeconds(_options.LeaseSeconds))
                {
                    _log.LogWarning(ex,
                        "Lease da sincronização {Request} sem renovação por mais que a sua duração; o trabalho é interrompido porque outro worker pode tê-lo assumido.",
                        lease.RequestId);
                    work.Cancel();
                    return;
                }
                _log.LogWarning(ex, "Falha ao renovar o lease da sincronização {Request}.", lease.RequestId);
                continue;
            }
            if (!renewed)
            {
                work.Cancel();
                return;
            }
            lastRenewed = _clock.GetTimestamp();
        }
    }
}
