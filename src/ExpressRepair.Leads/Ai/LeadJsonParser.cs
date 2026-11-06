using System.Text.Json;
using ExpressRepair.Leads.Domain;

namespace ExpressRepair.Leads.Ai;

/// <summary>
/// Turns raw LLM text into a validated <see cref="LeadExtraction"/>, or throws <see cref="LlmResponseException"/>.
/// LLM output is untrusted input: never assume it is valid JSON or follows the schema.
/// </summary>
public static class LeadJsonParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web); // camelCase, case-insensitive

    // Shape of the JSON we asked for; everything nullable because the model may omit anything.
    private sealed record RawLead(
        string? CustomerName, string? Appliance, string? Brand, string? Problem, string? Urgency, string? PreferredTime);

    public static LeadExtraction Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new LlmResponseException("LLM returned an empty response.");

        RawLead? lead;
        try
        {
            lead = JsonSerializer.Deserialize<RawLead>(StripCodeFence(raw), JsonOptions);
        }
        catch (JsonException)
        {
            throw new LlmResponseException($"LLM returned invalid JSON: \"{Preview(raw)}\"");
        }

        if (lead is null)
            throw new LlmResponseException("LLM returned JSON null instead of an object.");

        var problem = Clean(lead.Problem)
            ?? throw new LlmResponseException("LLM JSON is missing the required 'problem' field.");

        var urgency = Clean(lead.Urgency)?.ToLowerInvariant();
        if (urgency is null || !LeadExtraction.Urgencies.Contains(urgency))
            throw new LlmResponseException($"LLM returned unsupported urgency '{lead.Urgency}'. Expected low, medium or high.");

        // An unexpected appliance is not worth failing the lead over: file it under "other".
        var appliance = Clean(lead.Appliance)?.ToLowerInvariant();
        if (appliance is null || !LeadExtraction.Appliances.Contains(appliance))
            appliance = "other";

        return new LeadExtraction(
            CustomerName: Clean(lead.CustomerName),
            Appliance: appliance,
            Brand: Clean(lead.Brand),
            Problem: problem,
            Urgency: urgency,
            PreferredTime: Clean(lead.PreferredTime));
    }

    /// <summary>Models sometimes wrap JSON in ```json ... ``` even when told not to.</summary>
    private static string StripCodeFence(string raw)
    {
        var text = raw.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        var firstNewLine = text.IndexOf('\n');
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewLine >= 0 && lastFence > firstNewLine
            ? text[(firstNewLine + 1)..lastFence].Trim()
            : text;
    }

    /// <summary>Trims, and treats "", "null", "unknown" and "n/a" as missing.</summary>
    private static string? Clean(string? value)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) || v.Equals("null", StringComparison.OrdinalIgnoreCase)
               || v.Equals("unknown", StringComparison.OrdinalIgnoreCase) || v.Equals("n/a", StringComparison.OrdinalIgnoreCase)
            ? null
            : v;
    }

    private static string Preview(string raw)
    {
        var oneLine = raw.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 80 ? oneLine : oneLine[..80] + "…";
    }
}
