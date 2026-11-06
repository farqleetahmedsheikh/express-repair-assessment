using System.Text.RegularExpressions;

namespace ExpressRepair.Leads.Ai.Mock;

/// <summary>
/// Expected result of a sample, shown on the Simulate panel.
/// </summary>
public enum SampleOutcome { Done, FailsThenRecovers, AlwaysFails }

/// <summary>A fake "recording" the mock transcription service knows how to transcribe.</summary>
public sealed record SampleRecording(
    string Key,
    string Title,
    string Caller,
    string From,
    int DurationSeconds,
    string Transcript,
    SampleOutcome Outcome,
    string OutcomeNote);

/// <summary>
/// The three transcripts from the task, plus two failure scenarios for demoing error handling.
/// The Simulate button puts the sample key into the RecordingUrl (".../RE123-sample-2.mp3"),
/// so the request still goes through the real webhook and the mock "downloads" the right recording.
/// </summary>
public static partial class SampleRecordings
{
    public static readonly IReadOnlyList<SampleRecording> All =
    [
        new("1", "Samsung fridge not cooling", "Maria Lopez", "+14165550123", 47,
            "Hi, this is Maria Lopez. My Samsung fridge stopped cooling since yesterday, all my food is going bad. Please call me back as soon as possible, I'm home all day.",
            SampleOutcome.Done, "Sample transcript 1"),

        new("2", "Whirlpool washer not draining", "John", "+16475550142", 38,
            "Hello, I have a Whirlpool washer, it doesn't drain water at the end of the cycle. No rush, maybe next week in the morning. My name is John.",
            SampleOutcome.Done, "Sample transcript 2"),

        new("3", "Oven making a noise", "Unknown caller", "+19055550178", 12,
            "Yeah hi... uh, the oven, it's making a weird noise, I don't know the brand. Call me.",
            SampleOutcome.Done, "Sample transcript 3"),

        new("garbled", "Garbled recording", "Unknown caller", "+14375550111", 9,
            "[inaudible] ... yeah ... [static] ... the, uh ... [inaudible] ... okay thanks.",
            SampleOutcome.AlwaysFails, "LLM returns invalid JSON → Failed"),

        new("outage", "Transcription outage", "Priya Shah", "+12895550190", 31,
            "Hi, it's Priya Shah. My LG dryer won't heat up, the clothes come out wet. Tomorrow afternoon works best.",
            SampleOutcome.FailsThenRecovers, "Provider 503 → Failed, Retry → Done"),
    ];

    public static SampleRecording Default => All[0];

    public static SampleRecording? Find(string key) =>
        All.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the sample key from a URL like https://example.com/recordings/RE123-sample-2.mp3.</summary>
    public static SampleRecording? FromRecordingUrl(string recordingUrl)
    {
        var match = SampleKeyRegex().Match(recordingUrl);
        return match.Success ? Find(match.Groups["key"].Value) : null;
    }

    [GeneratedRegex(@"sample-(?<key>[a-z0-9]+)\.mp3", RegexOptions.IgnoreCase)]
    private static partial Regex SampleKeyRegex();
}
