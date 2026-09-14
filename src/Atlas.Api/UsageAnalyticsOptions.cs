using System.Text.Json.Serialization;

namespace Vev.Atlas.Api;

/// <summary>Optional public collector destination. There is no default analytics service.</summary>
public sealed record UsageAnalyticsOptions([property: JsonPropertyName("endpoint")] string Endpoint)
{
    public static UsageAnalyticsOptions? FromConfiguration(IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Atlas:UsageAnalytics:Enabled")) return null;
        var endpoint = configuration["Atlas:UsageAnalytics:Endpoint"];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return null;
        return new UsageAnalyticsOptions(uri.AbsoluteUri);
    }
}
