namespace ExpressRepair.Leads.Domain;

/// <summary>
/// A validated lead extracted from a transcript. Produced only by <c>LeadJsonParser</c>,
/// so every instance has a known appliance, a known urgency and a non-empty problem.
/// </summary>
public sealed record LeadExtraction(
    string? CustomerName,
    string Appliance,
    string? Brand,
    string Problem,
    string Urgency,
    string? PreferredTime)
{
    public static readonly IReadOnlyList<string> Appliances = ["fridge", "washer", "dryer", "dishwasher", "oven", "other"];
    public static readonly IReadOnlyList<string> Urgencies = ["low", "medium", "high"];
}
