using ExpressRepair.Leads.Data;
using ExpressRepair.Leads.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExpressRepair.Leads.Processing;

/// <summary>
/// Long-running background service: reads call ids from the queue and processes them
/// outside of any HTTP request.
/// </summary>
public sealed class CallProcessingWorker(
    ICallQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<ProcessingOptions> options,
    ILogger<CallProcessingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueUnfinishedCallsAsync(stoppingToken);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.Value.MaxConcurrency),
            CancellationToken = stoppingToken
        };

        try
        {
            await Parallel.ForEachAsync(queue.ReadAllAsync(stoppingToken), parallelOptions, ProcessOneAsync);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async ValueTask ProcessOneAsync(Guid callId, CancellationToken stoppingToken)
    {
        try
        {
            // The worker is a singleton but DbContext is scoped -> one DI scope per call.
            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<CallProcessor>();
            await processor.ProcessAsync(callId, stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // CallProcessor already turns AI failures into a Failed call. This is the last safety net:
            // an unhandled exception here would stop the whole host (.NET's default BackgroundService behaviour).
            logger.LogError(ex, "Unexpected error while processing call {CallId}", callId);
        }
    }

    /// <summary>
    /// The queue lives in memory, so after a restart any call still marked Processing would be stuck forever.
    /// </summary>
    private async Task RequeueUnfinishedCallsAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var unfinished = await db.Calls
            .Where(c => c.Status == CallStatus.Processing)
            .OrderBy(c => c.ReceivedAtUtc)
            .Select(c => c.Id)
            .ToListAsync(stoppingToken);

        foreach (var id in unfinished)
            queue.Enqueue(id);

        if (unfinished.Count > 0)
            logger.LogInformation("Re-queued {Count} call(s) left in Processing by a previous run", unfinished.Count);
    }
}
