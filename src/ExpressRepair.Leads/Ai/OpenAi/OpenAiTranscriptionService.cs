using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ExpressRepair.Leads.Ai.OpenAi;

/// <summary>
/// Real transcription: download the recording, then send it to OpenAI's /audio/transcriptions (Whisper).
/// Two typed HttpClients: one for the recording host, one for OpenAI (see AiServiceCollectionExtensions).
/// </summary>
public sealed class OpenAiTranscriptionService(
    IHttpClientFactory httpClientFactory,
    IOptions<OpenAiOptions> openAiOptions,
    IOptions<RecordingDownloadOptions> downloadOptions) : ITranscriptionService
{
    public const string OpenAiClientName = "openai";
    public const string RecordingClientName = "recordings";

    public async Task<string> TranscribeAsync(string recordingUrl, CancellationToken cancellationToken)
    {
        var audio = await DownloadRecordingAsync(recordingUrl, cancellationToken);

        var o = openAiOptions.Value;
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        form.Add(file, "file", "recording.mp3");
        form.Add(new StringContent(o.TranscriptionModel), "model");
        form.Add(new StringContent("en"), "language");

        using var request = new HttpRequestMessage(HttpMethod.Post, "audio/transcriptions") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);

        using var response = await httpClientFactory.CreateClient(OpenAiClientName).SendAsync(request, cancellationToken);
        await OpenAiErrors.EnsureSuccessAsync(response, "Transcription", cancellationToken);

        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var text = body.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
        return string.IsNullOrWhiteSpace(text)
            ? throw new InvalidOperationException("Transcription returned no text (silent or empty recording?).")
            : text.Trim();
    }

    private async Task<byte[]> DownloadRecordingAsync(string recordingUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(recordingUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("RecordingUrl must be an absolute HTTPS URL.");

        var d = downloadOptions.Value;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrEmpty(d.Username))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{d.Username}:{d.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        using var response = await httpClientFactory.CreateClient(RecordingClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Recording download failed: {(int)response.StatusCode} ({response.ReasonPhrase}).", null, response.StatusCode);

        if (response.Content.Headers.ContentLength > d.MaxBytes)
            throw new InvalidOperationException($"Recording is larger than {d.MaxBytes / (1024 * 1024)} MB.");

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return bytes.Length switch
        {
            0 => throw new InvalidOperationException("Recording download returned an empty file."),
            var n when n > d.MaxBytes => throw new InvalidOperationException($"Recording is larger than {d.MaxBytes / (1024 * 1024)} MB."),
            _ => bytes
        };
    }
}
