using System.Threading.Channels;
using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Documents;

/// <summary>
/// Gatilho de sincronização de políticas em processo (System.Threading.Channels). O controller publica o
/// tenant e um único leitor — o <c>PolicyIngestionWorker</c> — consome. Simples e sem dependências
/// externas; em produção troca-se por RabbitMQ/Azure Service Bus mantendo a interface
/// <see cref="IPolicySyncTrigger"/>. Este tipo só TRANSPORTA: a serialização e a idempotência do trabalho
/// são responsabilidade do consumidor (worker) — mesmo contrato do <see cref="ChannelDocumentAnalysisQueue"/>.
/// </summary>
public sealed class ChannelPolicySyncTrigger : IPolicySyncTrigger
{
    private readonly Channel<Guid> _channel =
        Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public ValueTask RequestSyncAsync(Guid tenantId, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(tenantId, ct);

    public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}
