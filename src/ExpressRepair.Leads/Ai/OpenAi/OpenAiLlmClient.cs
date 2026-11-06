using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ExpressRepair.Leads.Ai.OpenAi;

/// <summary>
/// Real LLM client: OpenAI Chat Completions in JSON mode.
/// Transient errors (timeouts, 429, 5xx) are retried by the resilience handler registered in DI;
/// a non-success status that survives the retries throws <see cref="HttpRequestException"/> → call ends Failed.
/// </summary>
public sealed class OpenAiLlmClient(HttpClient http, IOptions<OpenAiOptions> options) : ILlmClient
{
    public async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken)
    {
        var o = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = o.ChatModel,
                response_format = new { type = "json_object" }, // model must answer with a JSON object
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = $"Voicemail transcript:\n\"\"\"\n{userMessage}\n\"\"\"" }
                }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);

        using var response = await http.SendAsync(request, cancellationToken);
        await OpenAiErrors.EnsureSuccessAsync(response, "Chat completion", cancellationToken);

        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        try
        {
            // { "choices": [ { "message": { "content": "{...lead json...}" } } ] }
            return body.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        }
        catch (Exception ex) when (ex is KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new LlmResponseException("OpenAI response did not contain choices[0].message.content.");
        }
    }
}

internal static class OpenAiErrors
{
    /// <summary>Like EnsureSuccessStatusCode, but keeps OpenAI's error message (e.g. "Incorrect API key").</summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var detail = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var json = JsonDocument.Parse(detail);
            detail = json.RootElement.GetProperty("error").GetProperty("message").GetString() ?? detail;
        }
        catch (Exception) { /* not OpenAI's error shape — keep raw text */ }

        if (detail.Length > 300) detail = detail[..300] + "…";
        throw new HttpRequestException(
            $"{operation} failed: {(int)response.StatusCode} ({response.ReasonPhrase}). {detail}".Trim(),
            null, response.StatusCode);
    }
}
