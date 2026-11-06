namespace ExpressRepair.Leads.Ai.Mock;

/// <summary>Bound from "MockAi". Fake latency so you can actually watch a call sit in Processing.</summary>
public sealed class MockAiOptions
{
    public const string Section = "MockAi";

    public int TranscriptionDelayMs { get; set; } = 1500;
    public int LlmDelayMs { get; set; } = 1200;
}
