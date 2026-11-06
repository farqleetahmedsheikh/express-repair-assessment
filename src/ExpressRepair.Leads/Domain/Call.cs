namespace ExpressRepair.Leads.Domain;

public enum CallStatus
{
    Processing,
    Done,
    Failed
}

/// <summary>
/// One recorded voicemail from the phone provider plus the lead extracted from it.
/// </summary>
public sealed class Call
{
    private const int MaxErrorLength = 1000;

    public Guid Id { get; set; } = Guid.NewGuid();

    // --- Webhook data (what the phone provider tells us) ---
    public required string CallSid { get; set; }
    public required string From { get; set; }
    public string To { get; set; } = "";
    public required string RecordingSid { get; set; }
    public required string RecordingUrl { get; set; }
    public int? RecordingDurationSeconds { get; set; }

    // --- Processing state ---
    public CallStatus Status { get; set; } = CallStatus.Processing;
    public string? Error { get; set; }
    public int Attempts { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    // --- AI output ---
    public string? Transcript { get; set; }
    public string? CustomerName { get; set; }
    public string? Appliance { get; set; }
    public string? Brand { get; set; }
    public string? Problem { get; set; }
    public string? Urgency { get; set; }
    public string? PreferredTime { get; set; }

    public void MarkDone(LeadExtraction lead, DateTime nowUtc)
    {
        CustomerName = lead.CustomerName;
        Appliance = lead.Appliance;
        Brand = lead.Brand;
        Problem = lead.Problem;
        Urgency = lead.Urgency;
        PreferredTime = lead.PreferredTime;
        Status = CallStatus.Done;
        Error = null;
        CompletedAtUtc = nowUtc;
    }

    public void MarkFailed(string error, DateTime nowUtc)
    {
        Status = CallStatus.Failed;
        Error = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        CompletedAtUtc = nowUtc;
    }

    /// <summary>Puts a failed call back into the pipeline (manual retry from the UI).</summary>
    public void ResetForRetry()
    {
        Status = CallStatus.Processing;
        Error = null;
        CompletedAtUtc = null;
    }
}
