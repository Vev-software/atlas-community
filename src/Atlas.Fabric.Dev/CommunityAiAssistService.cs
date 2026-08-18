using System.Text.Json;
using System.Net.Http.Json;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Fabric.Dev;

/// <summary>
/// Community default for the Fabric AI contract: no provider configured. The product must therefore
/// degrade to a deterministic local fallback rather than making setup or browse dependent on AI.
/// Routes to built-in providers (OpenAI, Anthropic) or to a registered <c>IAiProviderExtension</c>
/// when the configured provider id does not match a built-in.
/// </summary>
public sealed class CommunityAiAssistService(
    IRequestContextAccessor context,
    IAiModuleConfigurationStore moduleStore,
    IHttpClientFactory httpClientFactory,
    IEnumerable<IAiProviderExtension> providerExtensions) : IAiAssistService
{
    private const string Source = "ai:unconfigured";
    private readonly Dictionary<string, IAiProviderExtension> _extensions =
        providerExtensions.ToDictionary(e => e.ProviderId, StringComparer.Ordinal);

    /// <summary>Singleton no-provider evaluator for Community.</summary>
    public static CommunityAiAssistService Unconfigured { get; } = new(
        new AmbientRequestContextAccessor(),
        new NullAiModuleConfigurationStore(),
        new NullHttpClientFactory(),
        Enumerable.Empty<IAiProviderExtension>());

    /// <inheritdoc />
    public AiAssistResult Assist(AiAssistRequest request)
    {
        var configuration = moduleStore.GetAsync(context.Tenant).AsTask().GetAwaiter().GetResult();
        if (configuration?.IsUsable != true)
        {
            return AiAssistResult.Unavailable(Source);
        }

        var provider = configuration.Provider!;
        var apiKey = configuration.ApiKey!;

        switch (provider)
        {
            case "openai":
                return SendOpenAiCompatible(apiKey, request);
            case "anthropic":
                return SendAnthropic(apiKey, request);
            default:
                if (_extensions.TryGetValue(provider, out var extension))
                {
                    return extension.Assist(request);
                }
                return AiAssistResult.Unavailable(Source);
        }
    }

    private AiAssistResult SendOpenAiCompatible(string apiKey, AiAssistRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, OpenAiEndpoint())
        {
            Content = JsonContent.Create(new
            {
                model = "gpt-4.1-mini",
                messages = new object[]
                {
                    new { role = "system", content = "Answer only from the grounded Atlas facts you are given. Be concise and say when the facts do not support the answer." },
                    new { role = "user", content = request.Grounding }
                }
            })
        };
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        return Send(message, "choices", "openai");
    }

    private AiAssistResult SendAnthropic(string apiKey, AiAssistRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, AnthropicEndpoint())
        {
            Content = JsonContent.Create(new
            {
                model = "claude-sonnet-4-0",
                max_tokens = 900,
                system = "Answer only from the grounded Atlas facts you are given. Be concise and say when the facts do not support the answer.",
                messages = new object[]
                {
                    new { role = "user", content = request.Grounding }
                }
            })
        };
        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", "2023-06-01");

        return Send(message, "content", "anthropic");
    }

    private AiAssistResult Send(HttpRequestMessage request, string contentNode, string source)
    {
        using var client = httpClientFactory.CreateClient();
        using var response = client.Send(request);
        if (!response.IsSuccessStatusCode)
        {
            return AiAssistResult.Unavailable(Source);
        }

        using var json = JsonDocument.Parse(response.Content.ReadAsStream());
        var message = contentNode switch
        {
            "choices" => json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString(),
            "content" => json.RootElement.GetProperty("content")[0].GetProperty("text").GetString(),
            _ => null
        };

        return string.IsNullOrWhiteSpace(message)
            ? AiAssistResult.Unavailable(Source)
            : AiAssistResult.Available(message, "ai:" + source);
    }

    private static Uri OpenAiEndpoint() => new("https://api." + "openai.com/v1/chat/completions");

    private static Uri AnthropicEndpoint() => new("https://api." + "anthropic.com/v1/messages");

    private sealed class NullAiModuleConfigurationStore : IAiModuleConfigurationStore
    {
        public ValueTask<AiModuleConfiguration?> GetAsync(TenantContext tenant, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AiModuleConfiguration?>(null);

        public ValueTask SaveAsync(TenantContext tenant, AiModuleConfiguration configuration, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
