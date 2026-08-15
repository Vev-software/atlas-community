using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vev.Atlas.Contracts;
using Vev.Atlas.Fabric.Dev;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// Setup copilot tests: grounded onboarding suggestions over the current tenant state, with a clean
/// static fallback when no AI provider is configured.
/// </summary>
public sealed class SetupCopilotTests(AtlasApiFactory factory) : IClassFixture<AtlasApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Client(string tenant = "acme", string principal = "arch", string roles = "AtlasArchitect")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        client.DefaultRequestHeaders.Add("X-Principal-Id", principal);
        client.DefaultRequestHeaders.Add("X-Principal-Roles", roles);
        return client;
    }

    [Fact]
    public async Task Setup_copilot_returns_static_grounded_onboarding_for_an_empty_tenant()
    {
        var response = await Client(tenant: "t-setup-empty").GetAsync("/api/v1/setup-copilot");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var guide = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("static", guide.GetProperty("mode").GetString());
        Assert.True(guide.GetProperty("snapshot").GetProperty("isEmpty").GetBoolean());
        Assert.Equal(0, guide.GetProperty("snapshot").GetProperty("assetCount").GetInt32());
        Assert.True(guide.GetProperty("suggestions").GetArrayLength() >= 3);

        var audit = factory.Services.GetRequiredService<InMemoryAuditSink>();
        Assert.Contains(audit.Events, e =>
            e.Action == "atlas.ai.assist.setup" &&
            e.TenantId == "t-setup-empty" &&
            e.Resource == "atlas:setup-copilot");
    }

    [Fact]
    public async Task Setup_copilot_is_grounded_in_the_current_tenant_state()
    {
        var client = Client(tenant: "t-setup-grounded");
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("sys-crm", AssetKind.System, "CRM", Lifecycle.Active), Json);
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("app-portal", AssetKind.Application, "Portal", Lifecycle.Active), Json);
        await client.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r1", "app-portal", "sys-crm", RelationshipType.PartOf), Json);

        var guide = await client.GetFromJsonAsync<JsonElement>("/api/v1/setup-copilot", Json);

        Assert.Equal(2, guide.GetProperty("snapshot").GetProperty("assetCount").GetInt32());
        Assert.Equal(1, guide.GetProperty("snapshot").GetProperty("relationshipCount").GetInt32());
        Assert.False(guide.GetProperty("snapshot").GetProperty("isEmpty").GetBoolean());
    }

    [Fact]
    public async Task Setup_copilot_is_available_to_a_read_only_customer()
    {
        var response = await Client(tenant: "t-setup-ro", principal: "viewer", roles: "AtlasCustomer")
            .GetAsync("/api/v1/setup-copilot");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var guide = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.False(guide.GetProperty("canAuthor").GetBoolean());
    }

    [Fact]
    public async Task Landscape_page_contains_the_setup_copilot_hook()
    {
        var response = await factory.CreateClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Setup copilot", body);
        Assert.Contains('"' + "/v1/setup-copilot" + '"', body);
        Assert.Contains('"' + "/v1/ai/allowances" + '"', body);
        Assert.Contains("AI allowances", body);
    }
}
