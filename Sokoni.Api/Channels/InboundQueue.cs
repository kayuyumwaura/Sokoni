using System.Threading.Channels;
using Sokoni.Agent;
using Sokoni.Domain;

namespace Sokoni.Channels;

/// <summary>
/// Webhooks must answer Meta within a few seconds, but a model call can take longer.
/// So the webhook only enqueues; this worker does the slow part.
/// </summary>
public sealed class InboundQueue
{
    private readonly Channel<IncomingMessage> _channel = Channel.CreateUnbounded<IncomingMessage>();
    public ValueTask EnqueueAsync(IncomingMessage msg, CancellationToken ct = default) => _channel.Writer.WriteAsync(msg, ct);
    public IAsyncEnumerable<IncomingMessage> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

public sealed class InboundWorker : BackgroundService
{
    private readonly InboundQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<InboundWorker> _log;

    public InboundWorker(InboundQueue queue, IServiceScopeFactory scopes, ILogger<InboundWorker> log)
    {
        _queue = queue;
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // One message at a time keeps each customer's conversation in order. Plenty for a pilot.
        await foreach (var msg in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<MessageProcessor>();
                await processor.ProcessAsync(msg, stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to process message {Id} from {Customer}", msg.ExternalId, msg.CustomerId);
            }
        }
    }
}
