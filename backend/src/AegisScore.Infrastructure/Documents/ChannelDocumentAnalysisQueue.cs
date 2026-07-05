using System.Threading.Channels;
using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Documents;

/// <summary>
/// Fila de leitura em processo (System.Threading.Channels). Simples e sem dependências externas;
/// em produção troca-se por RabbitMQ/Azure Service Bus mantendo a interface IDocumentAnalysisQueue.
/// </summary>
public sealed class ChannelDocumentAnalysisQueue : IDocumentAnalysisQueue
{
    private readonly Channel<Guid> _channel =
        Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public ValueTask EnqueueAsync(Guid documentId, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(documentId, ct);

    public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}
