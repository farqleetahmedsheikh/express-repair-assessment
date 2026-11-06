using ExpressRepair.Leads.Ai;
using ExpressRepair.Leads.Data;
using ExpressRepair.Leads.Domain;
using ExpressRepair.Leads.Realtime;
using Microsoft.Extensions.Options;

namespace ExpressRepair.Leads.Processing;

/// <summary>
/// Turns one recorded call into a lead: transcribe -> extract -> save Done, or save Failed with the reason.
/// Never throws for AI/transcription problems; those always end as a Failed call.
/// </summary>
public sealed class CallProcessor(
    AppDbContext db,
    ITranscriptionService transcription,
    LeadExtractor extractor,
    ICallNotifier notifier,
    IOptions<ProcessingOptions> options,
    TimeProvider clock,
    ILogger<CallProcessor> logger)
{
    public async Task ProcessAsync(Guid callId, CancellationToken stoppingToken)
    {
        var call = await db.Calls.FindAsync([callId], stoppingToken);
        if (call is null || call.Status != CallStatus.Processing)
            return; // deleted, or already handled (e.g. queued twice)

        call.Attempts++;
        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.TimeoutSeconds));

        try
        {
            // One deadline for the whole pipeline, linked to app shutdown.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeoutCts.CancelAfter(timeout);

            call.Transcript = await transcription.TranscribeAsync(call.RecordingUrl, timeoutCts.Token);
            var lead = await extractor.ExtractAsync(call.Transcript, timeoutCts.Token);

            call.MarkDone(lead, Now);
            logger.LogInformation("Call {CallId} processed: {Appliance}, urgency {Urgency}", call.Id, lead.Appliance, lead.Urgency);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // App is shutting down: leave the call in Processing; the worker re-queues it on next start.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own deadline fired (or HttpClient timed out).
            call.MarkFailed($"Timed out after {timeout.TotalSeconds:0} seconds.", Now);
            logger.LogWarning("Call {CallId} timed out after {Timeout}", call.Id, timeout);
        }
        catch (Exception ex)
        {
            call.MarkFailed(ex.Message, Now);
            logger.LogWarning(ex, "Call {CallId} failed", call.Id);
        }

        // Save even if the request that queued this call is long gone; don't let shutdown abort the write.
        await db.SaveChangesAsync(CancellationToken.None);
        await notifier.CallChangedAsync(call);
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
}
