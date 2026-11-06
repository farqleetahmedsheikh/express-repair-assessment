namespace ExpressRepair.Leads.Processing;

/// <summary>Bound from the "Processing" section of appsettings.json.</summary>
public sealed class ProcessingOptions
{
    public const string Section = "Processing";

    /// <summary>Hard limit for transcription + extraction of one call. After this the call is marked Failed.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>How many times we ask the LLM again when it returns unusable JSON.</summary>
    public int MaxLlmAttempts { get; set; } = 2;

    /// <summary>How many calls the background worker processes in parallel.</summary>
    public int MaxConcurrency { get; set; } = 2;
}
