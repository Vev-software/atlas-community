using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vev.Atlas.Contracts;
using Vev.Atlas.Domain;
using Vev.Atlas.Fabric;
using Vev.Atlas.Fabric.Dev;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// Context-pack tests: bounded, tenant-grounded slice export for deterministic hand-off, with a
/// narrative mode that stays behind the AI seam.
/// </summary>
public sealed class ContextPackTests(AtlasApiFactory factory) : IClassFixture<AtlasApiFactory>
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
    public async Task Deterministic_context_pack_includes_the_shortest_path_between_selected_assets()
    {
        var client = Client(tenant: "t-pack-path");
        await client.PostAsJsonAsync("/api/v1/assets", new Asset("app", AssetKind.Application, "App", Lifecycle.Active), Json);
        await client.PostAsJsonAsync("/api/v1/assets", new Asset("mid", AssetKind.Server, "Mid", Lifecycle.Active), Json);
        await client.PostAsJsonAsync("/api/v1/assets", new Asset("srv", AssetKind.Server, "Srv", Lifecycle.Active), Json);
        await client.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r1", "app", "mid", RelationshipType.DependsOn), Json);
        await client.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r2", "mid", "srv", RelationshipType.RunsOn), Json);

        var pack = await client.GetFromJsonAsync<JsonElement>("/api/v1/context-pack?assetId=app&assetId=srv", Json);

        Assert.Equal("deterministic", pack.GetProperty("mode").GetString());
        Assert.Equal(3, pack.GetProperty("assets").GetArrayLength());
        Assert.Equal(2, pack.GetProperty("relationships").GetArrayLength());
        Assert.Contains("mid", pack.GetProperty("markdown").GetString()!);

        var audit = factory.Services.GetRequiredService<InMemoryAuditSink>();
        Assert.Contains(audit.Events, e =>
            e.Action == "atlas.context.export" &&
            e.TenantId == "t-pack-path" &&
            e.Resource.Contains("mode=deterministic", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Context_pack_can_be_seeded_from_a_selected_relationship()
    {
        var client = Client(tenant: "t-pack-rel");
        await client.PostAsJsonAsync("/api/v1/assets", new Asset("app", AssetKind.Application, "App", Lifecycle.Active), Json);
        await client.PostAsJsonAsync("/api/v1/assets", new Asset("srv", AssetKind.Server, "Srv", Lifecycle.Active), Json);
        await client.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r-rel", "app", "srv", RelationshipType.RunsOn, "runtime path"), Json);

        var pack = await client.GetFromJsonAsync<JsonElement>("/api/v1/context-pack?relationshipId=r-rel", Json);

        Assert.Equal(2, pack.GetProperty("assets").GetArrayLength());
        Assert.Equal(1, pack.GetProperty("relationships").GetArrayLength());
        Assert.Contains("runtime path", pack.GetProperty("markdown").GetString()!);
    }

    [Fact]
    public async Task Read_only_customer_can_export_a_context_pack()
    {
        var author = Client(tenant: "t-pack-ro");
        await author.PostAsJsonAsync("/api/v1/assets", new Asset("app", AssetKind.Application, "App", Lifecycle.Active), Json);

        var response = await Client(tenant: "t-pack-ro", principal: "viewer", roles: "AtlasCustomer")
            .GetAsync("/api/v1/context-pack?assetId=app");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Narrative_context_pack_is_entitlement_denied_in_community()
    {
        var client = Client(tenant: "t-pack-narrative");
        await client.PostAsJsonAsync("/api/v1/assets", new Asset("app", AssetKind.Application, "App", Lifecycle.Active), Json);

        var response = await client.GetAsync("/api/v1/context-pack?assetId=app&mode=narrative");

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("atlas.ai.brief", body.GetProperty("capability").GetString());
        Assert.Equal("entitlement_denied", body.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task Narrative_context_pack_degrades_cleanly_when_ai_is_not_configured()
    {
        using var entitledFactory = new NarrativeEntitledFactory();
        var client = entitledFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-pack-ai");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "arch");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");

        await client.PostAsJsonAsync("/api/v1/assets", new Asset("app", AssetKind.Application, "App", Lifecycle.Active), Json);

        var pack = await client.GetFromJsonAsync<JsonElement>("/api/v1/context-pack?assetId=app&mode=narrative", Json);

        Assert.Equal("narrative", pack.GetProperty("mode").GetString());
        Assert.Equal("ai_not_configured", pack.GetProperty("narrativeStatus").GetString());
        Assert.Contains("App", pack.GetProperty("markdown").GetString()!);
    }

    [Fact]
    public async Task Landscape_page_contains_the_context_pack_hook()
    {
        var response = await factory.CreateClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Context pack", body);
        Assert.Contains("/api/v1/context-pack", body);
    }

    private sealed class NarrativeEntitledFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<Microsoft.EntityFrameworkCore.DbContextOptions<Vev.Atlas.Persistence.AtlasDbContext>>();
                services.AddDbContext<Vev.Atlas.Persistence.AtlasDbContext>(options => options.UseSqlite(_connection));
                var entitlements = new CommunityEntitlementService(
                    new HashSet<string>(StringComparer.Ordinal)
                    {
                        AtlasCapabilities.AiBrief.Value
                    });
                services.RemoveAll<IEntitlementService>();
                services.RemoveAll<IEntitlementAllowanceProvider>();
                services.AddSingleton<IEntitlementService>(entitlements);
                services.AddSingleton<IEntitlementAllowanceProvider>(entitlements);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _connection.Dispose();
            }
        }
    }
}
