using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Fabric.Portic;

/// <summary>
/// Portic gateway adapter for the Fabric AI contract. Sends requests to a configurable Portic
/// endpoint using raw HttpClient — no provider SDK.
/// </summary>
public sealed class PorticAiProviderExtension(
    IOptions<PorticOptions> options,
    IHttpClientFactory httpClientFactory) : IAiProviderExtension
{
    private const string Provider = "portic";
    private const string Source = "ai:unconfigured";

    public string ProviderId => Provider;

    public AiAssistResult Assist(AiAssistRequest request)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.BaseUrl))
        {
            return AiAssistResult.Unavailable(Source);
        }

        var endpoint = new Uri(new Uri(opts.BaseUrl.TrimEnd('/')), "/chat/completions");

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                model = opts.Model ?? "gpt-4.1-mini",
                max_tokens = opts.MaxTokens,
                messages = new object[]
                {
                    new { role = "system", content = "Answer only from the grounded Atlas facts you are given. Be concise and say when the facts do not support the answer." },
                    new { role = "user", content = request.Grounding }
                }
            })
        };

        using var client = httpClientFactory.CreateClient();
        using var response = client.Send(message);
        if (!response.IsSuccessStatusCode)
        {
            return AiAssistResult.Unavailable(Source);
        }

        using var json = JsonDocument.Parse(response.Content.ReadAsStream());
        string? responseText = null;

        try
        {
            responseText = json.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                ? choices[0].TryGetProperty("message", out var msg) ? msg.TryGetProperty("content", out var content) ? content.GetString() : null : null
                : null;
        }
        catch
        {
            responseText = null;
        }

        return string.IsNullOrWhiteSpace(responseText)
            ? AiAssistResult.Unavailable(Source)
            : AiAssistResult.Available(responseText, "ai:" + Provider);
    }
}
