using System.ComponentModel.DataAnnotations;
using System.Globalization;
using ExpressRepair.Leads.Data;
using ExpressRepair.Leads.Domain;
using ExpressRepair.Leads.Processing;
using ExpressRepair.Leads.Realtime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExpressRepair.Leads.Api;

/// <summary>Fields the phone provider sends when a recording is ready (Twilio naming).</summary>
public sealed class RecordingWebhookRequest
{
    [Required, MaxLength(64)] public string CallSid { get; set; } = "";
    [Required, MaxLength(32)] public string From { get; set; } = "";
    [MaxLength(32)] public string? To { get; set; }
    [Required, MaxLength(64)] public string RecordingSid { get; set; } = "";
    [Required, Url, MaxLength(2048)] public string RecordingUrl { get; set; } = "";
    public string? RecordingDuration { get; set; }
}

public sealed record WebhookAcceptedResponse(Guid Id, string Status, bool Duplicate);

/// <summary>
/// The endpoint the phone provider calls. It only stores the call and queues it —
/// no transcription or AI here, so it answers in milliseconds.
/// </summary>
[ApiController]
[Route("api/calls/recording")]
public sealed class RecordingWebhookController(
    AppDbContext db,
    ICallQueue queue,
    ICallNotifier notifier,
    TimeProvider clock,
    ILogger<RecordingWebhookController> logger) : ControllerBase
{
    /// <summary>Twilio's real format: application/x-www-form-urlencoded. Used by the Simulate button.</summary>
    [HttpPost]
    [Consumes("application/x-www-form-urlencoded")]
    public Task<IActionResult> FromForm([FromForm] RecordingWebhookRequest request, CancellationToken ct) =>
        AcceptAsync(request, ct);

    /// <summary>Same payload as JSON — handy for curl/Postman with the sample from the task.</summary>
    [HttpPost]
    [Consumes("application/json")]
    public Task<IActionResult> FromJson([FromBody] RecordingWebhookRequest request, CancellationToken ct) =>
        AcceptAsync(request, ct);

    private async Task<IActionResult> AcceptAsync(RecordingWebhookRequest request, CancellationToken ct)
    {
        // Idempotency: providers retry webhooks on timeouts/5xx. The same recording must not create two leads.
        var existing = await FindByRecordingSidAsync(request.RecordingSid.Trim(), ct);
        if (existing is not null)
            return Ok(existing);

        var call = new Call
        {
            CallSid = request.CallSid.Trim(),
            From = request.From.Trim(),
            To = request.To?.Trim() ?? "",
            RecordingSid = request.RecordingSid.Trim(),
            RecordingUrl = request.RecordingUrl.Trim(),
            RecordingDurationSeconds = int.TryParse(request.RecordingDuration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                ? seconds
                : null,
            Status = CallStatus.Processing,
            ReceivedAtUtc = clock.GetUtcNow().UtcDateTime
        };

        db.Calls.Add(call);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two deliveries of the same webhook raced and the unique index let only one in.
            // Answer 200 with the winner; anything else is a real database error.
            db.Entry(call).State = EntityState.Detached;
            var winner = await FindByRecordingSidAsync(call.RecordingSid, CancellationToken.None);
            if (winner is null)
                throw;
            return Ok(winner);
        }

        queue.Enqueue(call.Id);                // the background worker picks it up
        await notifier.CallChangedAsync(call); // the new "Processing" row appears in open browsers

        logger.LogInformation("Recording {RecordingSid} from {From} accepted as call {CallId}", call.RecordingSid, call.From, call.Id);
        return Ok(new WebhookAcceptedResponse(call.Id, call.Status.ToString(), Duplicate: false));
    }

    private Task<WebhookAcceptedResponse?> FindByRecordingSidAsync(string recordingSid, CancellationToken ct) =>
        db.Calls.AsNoTracking()
            .Where(c => c.RecordingSid == recordingSid)
            .Select(c => new WebhookAcceptedResponse(c.Id, c.Status.ToString(), true))
            .FirstOrDefaultAsync(ct);
}
