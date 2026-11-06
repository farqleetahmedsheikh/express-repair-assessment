namespace ExpressRepair.Leads.Ai.OpenAi;

/// <summary>
/// Bound from "OpenAI". ApiKey is intentionally NOT in appsettings.json — set it with
///   dotnet user-secrets set "OpenAI:ApiKey" "sk-..."   or the env var   OpenAI__ApiKey
/// BaseUrl can point at any OpenAI-compatible server (e.g. Azure OpenAI proxy, Ollama's /v1).
/// </summary>
public sealed class OpenAiOptions
{
    public const string Section = "OpenAI";

    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";
    public string ChatModel { get; set; } = "gpt-4o-mini";
    public string TranscriptionModel { get; set; } = "whisper-1";

    /// <summary>Per-HTTP-attempt timeout; the resilience handler retries transient failures within the total.</summary>
    public int AttemptTimeoutSeconds { get; set; } = 45;
    public int TotalTimeoutSeconds { get; set; } = 110;
}

/// <summary>
/// Bound from "RecordingDownload". Twilio protects recording URLs with HTTP Basic auth
/// (Account SID / Auth Token). Leave empty for public URLs.
/// </summary>
public sealed class RecordingDownloadOptions
{
    public const string Section = "RecordingDownload";

    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>OpenAI's transcription endpoint accepts files up to 25 MB.</summary>
    public long MaxBytes { get; set; } = 25 * 1024 * 1024;
}
