using Microsoft.Extensions.Logging;

namespace AegisScore.Infrastructure.Documents;

/// <summary>
/// [AEGIS-AUD-050] Batimento de lease: renova periodicamente o lease de um trabalho EM EXECUÇÃO para que uma
/// chamada lenta (IA, conector) que ultrapasse a duração do lease NÃO permita outra réplica adquirir o mesmo
/// item. Só definir um lease longo não basta — o batimento é a garantia real.
///
/// Contrato:
/// <list type="bullet">
/// <item>renova a cada <c>interval</c> (menor que a duração do lease), sempre GUARDADO pelo LeaseId (a
/// renovação vira no-op se o lease já não é nosso);</item>
/// <item>se a renovação indicar PERDA do lease (expirou e outra réplica assumiu), sinaliza o cancelamento do
/// processamento (<paramref name="leaseLostSignal"/>) — o worker para de trabalhar em algo que não é mais seu;</item>
/// <item>para no sucesso/falha/shutdown, ao dar <see cref="DisposeAsync"/>;</item>
/// <item>usa <see cref="TimeProvider"/> — testável sem sleeps reais.</item>
/// </list>
/// </summary>
public sealed class LeaseHeartbeat : IAsyncDisposable
{
    private readonly Task _loop;
    private readonly CancellationTokenSource _stop;

    private LeaseHeartbeat(Task loop, CancellationTokenSource stop)
    {
        _loop = loop;
        _stop = stop;
    }

    /// <summary>
    /// Inicia o batimento. <paramref name="renew"/> deve renovar o lease e devolver <c>false</c> quando ele já
    /// não é mais o vigente. <paramref name="leaseLostSignal"/> é o CTS que governa o processamento: o batimento
    /// o cancela se o lease for perdido, e para de bater quando ele já estiver cancelado (shutdown/lease perdido).
    /// </summary>
    public static LeaseHeartbeat Start(
        Func<CancellationToken, Task<bool>> renew, TimeSpan interval, TimeProvider clock,
        CancellationTokenSource leaseLostSignal, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(renew);
        ArgumentNullException.ThrowIfNull(leaseLostSignal);
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "O intervalo de batimento deve ser positivo.");

        // Para o loop quando o processamento sinaliza fim (Dispose → _stop) OU quando o próprio lease-lost é
        // cancelado (shutdown externo ligado a leaseLostSignal).
        var stop = CancellationTokenSource.CreateLinkedTokenSource(leaseLostSignal.Token);
        var loop = RunAsync(renew, interval, clock, leaseLostSignal, stop.Token, log);
        return new LeaseHeartbeat(loop, stop);
    }

    private static async Task RunAsync(
        Func<CancellationToken, Task<bool>> renew, TimeSpan interval, TimeProvider clock,
        CancellationTokenSource leaseLostSignal, CancellationToken stopToken, ILogger log)
    {
        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                await Task.Delay(interval, clock, stopToken);

                bool renewed;
                try
                {
                    renewed = await renew(stopToken);
                }
                catch (OperationCanceledException)
                {
                    break;   // parada normal (processamento terminou / shutdown)
                }
                catch (Exception ex)
                {
                    // Falha transitória na renovação (ex.: banco momentaneamente indisponível) não derruba o
                    // processamento; tenta de novo no próximo batimento, ainda ANTES da expiração real do lease.
                    log.LogWarning(ex, "Batimento de lease falhou; tentará novamente no próximo intervalo.");
                    continue;
                }

                if (!renewed)
                {
                    // Lease perdido: expirou e outra réplica o adquiriu. Cancela o processamento para não haver
                    // dois workers no mesmo item.
                    log.LogWarning(
                        "Lease perdido durante o processamento; cancelando o trabalho para evitar duplicidade.");
                    leaseLostSignal.Cancel();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopToken cancelado durante o Task.Delay — parada normal.
        }
    }

    /// <summary>Para o batimento (sucesso/falha/shutdown) e aguarda o loop encerrar.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested) _stop.Cancel();
        try { await _loop; }
        catch { /* desfechos já tratados no loop */ }
        _stop.Dispose();
    }
}
