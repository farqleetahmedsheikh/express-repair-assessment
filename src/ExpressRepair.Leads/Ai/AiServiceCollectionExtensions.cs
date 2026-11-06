using ExpressRepair.Leads.Ai.Mock;
using ExpressRepair.Leads.Ai.OpenAi;
using Microsoft.Extensions.Http.Resilience;

namespace ExpressRepair.Leads.Ai;

/// <summary>
/// Chooses Mock or OpenAI for each interface from config:
///   "Ai": { "TranscriptionProvider": "Mock" | "OpenAI", "LlmProvider": "Mock" | "OpenAI" }
/// They are separate switches so you can run a real LLM with mock transcription
/// (the demo recording URLs are fake, so there is no real audio to transcribe).
/// </summary>
public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddAiProviders(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<MockAiOptions>(config.GetSection(MockAiOptions.Section));
        services.Configure<OpenAiOptions>(config.GetSection(OpenAiOptions.Section));
        services.Configure<RecordingDownloadOptions>(config.GetSection(RecordingDownloadOptions.Section));

        var transcriptionProvider = config["Ai:TranscriptionProvider"] ?? "Mock";
        var llmProvider = config["Ai:LlmProvider"] ?? "Mock";

        var useOpenAi = IsOpenAi(transcriptionProvider) || IsOpenAi(llmProvider);
        if (useOpenAi && string.IsNullOrWhiteSpace(config["OpenAI:ApiKey"]))
            throw new InvalidOperationException(
                "An AI provider is set to OpenAI but OpenAI:ApiKey is missing. " +
                "Run: dotnet user-secrets set \"OpenAI:ApiKey\" \"sk-...\" (or set the OpenAI__ApiKey env var), " +
                "or set Ai:TranscriptionProvider / Ai:LlmProvider back to Mock.");

        if (IsOpenAi(transcriptionProvider))
        {
            services.AddHttpClient(OpenAiTranscriptionService.OpenAiClientName, ConfigureOpenAiClient(config))
                    .AddStandardResilienceHandler(ConfigureResilience(config));
            services.AddHttpClient(OpenAiTranscriptionService.RecordingClientName)
                    .AddStandardResilienceHandler();
            services.AddSingleton<ITranscriptionService, OpenAiTranscriptionService>();
        }
        else
        {
            services.AddSingleton<ITranscriptionService, MockTranscriptionService>();
        }

        if (IsOpenAi(llmProvider))
        {
            services.AddHttpClient<ILlmClient, OpenAiLlmClient>(ConfigureOpenAiClient(config))
                    .AddStandardResilienceHandler(ConfigureResilience(config));
        }
        else
        {
            services.AddSingleton<ILlmClient, MockLlmClient>();
        }

        services.AddScoped<LeadExtractor>();
        return services;
    }

    private static bool IsOpenAi(string provider) => provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase);

    private static Action<HttpClient> ConfigureOpenAiClient(IConfiguration config) => client =>
    {
        var baseUrl = config["OpenAI:BaseUrl"] ?? "https://api.openai.com/v1/";
        client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        client.Timeout = Timeout.InfiniteTimeSpan; // timeouts are owned by the resilience handler below
    };

    /// <summary>
    /// Retries timeouts / 429 / 5xx with backoff. Defaults (10s per attempt, 30s total) are too short for
    /// LLM and Whisper calls, so they come from config. The circuit breaker's sampling window must be
    /// at least twice the attempt timeout, otherwise options validation fails.
    /// </summary>
    private static Action<HttpStandardResilienceOptions> ConfigureResilience(IConfiguration config) => o =>
    {
        var attempt = TimeSpan.FromSeconds(config.GetValue("OpenAI:AttemptTimeoutSeconds", 45));
        var total = TimeSpan.FromSeconds(config.GetValue("OpenAI:TotalTimeoutSeconds", 110));
        o.AttemptTimeout.Timeout = attempt;
        o.TotalRequestTimeout.Timeout = total;
        o.CircuitBreaker.SamplingDuration = attempt * 2;
    };
}
