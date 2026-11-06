using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ExpressRepair.Leads.Ai.Mock;

/// <summary>
/// Stand-in for a real LLM so the app runs end-to-end with no API key.
/// It uses simple keyword rules — not smart, just deterministic — and, like a real model,
/// returns a raw string: usually JSON, but plain prose for an unintelligible transcript.
/// That raw string goes through the same <see cref="LeadJsonParser"/> as a real model's answer.
/// </summary>
public sealed partial class MockLlmClient(IOptions<MockAiOptions> options) : ILlmClient
{
    private static readonly string[] Brands =
        ["Samsung", "Whirlpool", "LG", "GE", "Bosch", "Frigidaire", "Maytag", "KitchenAid", "Kenmore", "Electrolux", "Miele", "Haier"];

    public async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken)
    {
        await Task.Delay(options.Value.LlmDelayMs, cancellationToken);
        var transcript = userMessage;

        // A real model asked for JSON about an unintelligible voicemail often answers in prose.
        if (transcript.Contains("[inaudible]", StringComparison.OrdinalIgnoreCase))
            return "I'm sorry, but the transcript is mostly inaudible, so I can't identify the appliance or the problem.";

        var lead = new
        {
            customerName = Match(NameRegex(), transcript),
            appliance = DetectAppliance(transcript),
            brand = Brands.FirstOrDefault(b => Regex.IsMatch(transcript, $@"\b{b}\b", RegexOptions.IgnoreCase)),
            problem = DetectProblem(transcript),
            urgency = DetectUrgency(transcript),
            preferredTime = Capitalize(Match(PreferredTimeRegex(), transcript))
        };
        return JsonSerializer.Serialize(lead);
    }

    private static string DetectAppliance(string text) => text.ToLowerInvariant() switch
    {
        var t when Regex.IsMatch(t, @"\bdish ?washer\b") => "dishwasher", // check before "washer"
        var t when Regex.IsMatch(t, @"\b(fridge|refrigerator|freezer)\b") => "fridge",
        var t when Regex.IsMatch(t, @"\b(washer|washing machine)\b") => "washer",
        var t when Regex.IsMatch(t, @"\bdryer\b") => "dryer",
        var t when Regex.IsMatch(t, @"\b(oven|stove|range|cooktop)\b") => "oven",
        _ => "other"
    };

    private static string DetectUrgency(string text) => text.ToLowerInvariant() switch
    {
        var t when Regex.IsMatch(t, @"as soon as possible|\basap\b|urgent|emergency|going bad|spoil|leak|flood|smoke|sparks?") => "high",
        var t when Regex.IsMatch(t, @"no rush|not urgent|whenever|next week") => "low",
        _ => "medium"
    };

    /// <summary>First clause that sounds like a fault ("stopped cooling", "doesn't drain", ...).</summary>
    private static string DetectProblem(string text)
    {
        var clause = text.Split(['.', ',', '!', '?', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(c => ProblemRegex().IsMatch(c));
        return Capitalize(clause) ?? (text.Length > 100 ? text[..100] + "…" : text);
    }

    private static string? Match(Regex regex, string text)
    {
        var m = regex.Match(text);
        return m.Success ? m.Groups["value"].Value.Trim() : null;
    }

    private static string? Capitalize(string? s) =>
        string.IsNullOrEmpty(s) ? null : char.ToUpper(s[0], CultureInfo.InvariantCulture) + s[1..];

    // "this is Maria Lopez", "my name is John", "it's Priya Shah" — the name itself must be capitalised.
    [GeneratedRegex(@"(?i:\b(?:this is|my name is|it's|it is)\s+)(?<value>[A-Z][a-z]+(?:\s+[A-Z][a-z]+)?)")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"\b(?<value>(?:next week|this week|tomorrow|today|tonight|(?:mon|tues|wednes|thurs|fri|satur|sun)day)(?:\s+(?:in the\s+)?(?:morning|afternoon|evening))?|home all day|any ?time)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PreferredTimeRegex();

    [GeneratedRegex(@"\b(stopped|doesn't|does not|won't|will not|isn't|not working|broken|making|noise|leak\w*|smell\w*|stuck|error)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProblemRegex();
}
