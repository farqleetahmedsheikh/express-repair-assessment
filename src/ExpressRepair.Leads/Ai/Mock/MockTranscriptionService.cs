using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;

namespace ExpressRepair.Leads.Ai.Mock;

/// <summary>
/// Pretends to download and transcribe the recording. Picks the sample transcript from the RecordingUrl;
/// any other URL (e.g. the sample payload from the task) gets transcript 1.
/// </summary>
public sealed class MockTranscriptionService(IOptions<MockAiOptions> options, ILogger<MockTranscriptionService> logger)
    : ITranscriptionService
{
    // Singleton state: how many times each recording was attempted (drives the "outage" scenario).
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public async Task<string> TranscribeAsync(string recordingUrl, CancellationToken cancellationToken)
    {
        await Task.Delay(options.Value.TranscriptionDelayMs, cancellationToken);

        var sample = SampleRecordings.FromRecordingUrl(recordingUrl);
        if (sample is null)
        {
            logger.LogInformation("No sample key in {RecordingUrl}; using sample 1", recordingUrl);
            return SampleRecordings.Default.Transcript;
        }

        var attempt = _attempts.AddOrUpdate(recordingUrl, 1, (_, n) => n + 1);
        if (sample.Outcome == SampleOutcome.FailsThenRecovers && attempt == 1)
        {
            // Same exception type a real HttpClient throws on a 5xx with EnsureSuccessStatusCode().
            throw new HttpRequestException(
                "Transcription provider unavailable: 503 (Service Unavailable).", null, HttpStatusCode.ServiceUnavailable);
        }

        return sample.Transcript;
    }
}
