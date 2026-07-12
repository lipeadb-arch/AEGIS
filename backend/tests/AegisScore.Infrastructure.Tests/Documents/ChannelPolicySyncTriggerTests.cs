using AegisScore.Infrastructure.Documents;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Documents;

/// <summary>
/// Prova o canal de mensageria em memória do gatilho de sincronização de políticas
/// (<see cref="ChannelPolicySyncTrigger"/>): o que o controller publica, o worker consome — em ordem (FIFO).
/// </summary>
public sealed class ChannelPolicySyncTriggerTests
{
    [Fact]
    public async Task RequestSyncAsync_PublicaTenants_ConsumidorRecebeNaOrdem()
    {
        var trigger = new ChannelPolicySyncTrigger();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        await trigger.RequestSyncAsync(a);
        await trigger.RequestSyncAsync(b);

        using var cts = new CancellationTokenSource();
        var received = new List<Guid>();
        try
        {
            await foreach (var tenantId in trigger.DequeueAllAsync(cts.Token))
            {
                received.Add(tenantId);
                if (received.Count == 2) cts.Cancel();   // encerra o fluxo após consumir os dois
            }
        }
        catch (OperationCanceledException)
        {
            // Esperado: o cancelamento é como o worker encerra o consumo no shutdown.
        }

        received.Should().Equal(new[] { a, b }, "o canal preserva a ordem de publicação (FIFO)");
    }
}
