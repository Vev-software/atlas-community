using Microsoft.Extensions.Configuration;
using Vev.Atlas.Api;
using Xunit;

namespace Vev.Atlas.Api.Tests;

public sealed class UsageAnalyticsTests
{
    [Theory]
    [InlineData(false, "https://collector.example/events", false)]
    [InlineData(true, null, false)]
    [InlineData(true, "http://collector.example/events", false)]
    [InlineData(true, "https://user:secret@collector.example/events", false)]
    [InlineData(true, "https://collector.example/events?token=secret", false)]
    [InlineData(true, "https://collector.example/events#fragment", false)]
    [InlineData(true, "https://collector.example/events", true)]
    public void Only_explicit_enablement_and_a_clean_https_destination_enable_collection(bool enabled, string? endpoint, bool expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Atlas:UsageAnalytics:Enabled"] = enabled.ToString(),
            ["Atlas:UsageAnalytics:Endpoint"] = endpoint
        }).Build();
        Assert.Equal(expected, UsageAnalyticsOptions.FromConfiguration(configuration) is not null);
    }

    [Fact]
    public async Task Default_runtime_config_contains_no_analytics_destination()
    {
        using var factory = new AtlasApiFactory();
        using var client = factory.CreateClient();
        Assert.DoesNotContain("usageAnalytics", await client.GetStringAsync("/app-config.js"));
    }
}
