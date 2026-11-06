using ExpressRepair.Leads.Domain;
using ExpressRepair.Leads.Processing;
using Microsoft.Extensions.Options;

namespace ExpressRepair.Leads.Ai;

/// <summary>
/// Owns the prompt and the "ask again if the answer is unusable" rule.
/// Adding a new extracted field means: prompt below + <see cref="LeadExtraction"/> + <see cref="LeadJsonParser"/>.
/// </summary>
public sealed class LeadExtractor(ILlmClient llm, IOptions<ProcessingOptions> options, ILogger<LeadExtractor> logger)
{
    public const string SystemPrompt = """
        You extract service leads for an appliance repair company from voicemail transcripts.
        Respond with ONLY a JSON object (no prose, no markdown) with exactly these keys:
          "customerName":  the caller's name as said, or null
          "appliance":     one of "fridge", "washer", "dryer", "dishwasher", "oven", "other"
          "brand":         the appliance brand, or null
          "problem":       a short description of the problem (max ~15 words)
          "urgency":       one of "low", "medium", "high"
          "preferredTime": when the caller wants a visit or call back, or null
        Urgency guide: "high" = asks for ASAP, food spoiling, leaking/flooding, safety risk;
        "low" = caller says no rush or suggests a later date; otherwise "medium".
        Use null for anything the caller did not say. Never invent names, brands or times.
        """;

    public async Task<LeadExtraction> ExtractAsync(string transcript, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            throw new LlmResponseException("Transcript is empty, nothing to extract.");

        var maxAttempts = Math.Max(1, options.Value.MaxLlmAttempts);

        for (var attempt = 1; ; attempt++)
        {
            var raw = await llm.CompleteAsync(SystemPrompt, transcript, cancellationToken);
            try
            {
                return LeadJsonParser.Parse(raw);
            }
            catch (LlmResponseException ex) when (attempt < maxAttempts)
            {
                // LLMs are non-deterministic: an invalid answer is often fixed by simply asking again.
                logger.LogWarning("Unusable LLM response (attempt {Attempt}/{Max}): {Reason}. Retrying.", attempt, maxAttempts, ex.Message);
            }
            catch (LlmResponseException ex)
            {
                throw new LlmResponseException(
                    maxAttempts == 1 ? ex.Message : $"{ex.Message} (gave up after {maxAttempts} attempts)");
            }
        }
    }
}
