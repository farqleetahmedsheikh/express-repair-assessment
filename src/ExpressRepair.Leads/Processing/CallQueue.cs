using System.Threading.Channels;

namespace ExpressRepair.Leads.Processing;

/// <summary>Hands call ids from the webhook (producer) to the background worker (consumer).</summary>
public interface ICallQueue
{
    void Enqueue(Guid callId);
    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// In-memory queue built on <see cref="Channel{T}"/>. Enqueue never blocks, so the webhook stays fast.
/// It is not durable — the worker re-queues calls still in Processing on startup to cover restarts.
/// A production system would use a durable queue (Hangfire, Azure Service Bus, SQS, ...).
/// </summary>
public sealed class ChannelCallQueue : ICallQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    public void Enqueue(Guid callId)
    {
        // An unbounded channel only refuses writes after it is completed (app shutdown).
        if (!_channel.Writer.TryWrite(callId))
            throw new InvalidOperationException("The call queue is closed.");
    }

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
