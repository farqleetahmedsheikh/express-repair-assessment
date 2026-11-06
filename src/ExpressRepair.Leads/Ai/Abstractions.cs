namespace ExpressRepair.Leads.Ai;

/// <summary>Speech-to-text for a recorded call.</summary>
public interface ITranscriptionService
{
    Task<string> TranscribeAsync(string recordingUrl, CancellationToken cancellationToken);
}

/// <summary>
/// A plain chat-completion client: system prompt + user message in, raw model text out.
/// It deliberately returns the raw string — parsing and validation live in <see cref="LeadJsonParser"/>,
/// so mock and real clients go through exactly the same error handling.
/// </summary>
public interface ILlmClient
{
    Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken);
}

/// <summary>The LLM answered, but the answer is not a usable lead (invalid JSON, missing/invalid fields).</summary>
public sealed class LlmResponseException(string message) : Exception(message);
