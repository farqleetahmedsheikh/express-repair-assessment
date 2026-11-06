using ExpressRepair.Leads.Ai.Mock;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ExpressRepair.Leads.Pages;

public sealed class IndexModel(IConfiguration config) : PageModel
{
    public IReadOnlyList<SampleRecording> Samples => SampleRecordings.All;
    public string TranscriptionProvider => config["Ai:TranscriptionProvider"] ?? "Mock";
    public string LlmProvider => config["Ai:LlmProvider"] ?? "Mock";

    public void OnGet() { }
}
