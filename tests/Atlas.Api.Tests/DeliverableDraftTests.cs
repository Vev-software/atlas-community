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
/// Paid deliverable-draft tests: entitlement-gated in Community, grounded in the tenant slice, and
/// degraded cleanly when no AI provider is configured.
/// </summary>
public sealed class DeliverableDraftTests(AtlasApiFactory factory) : IClassFixture<AtlasApiFactory>
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
    public async Task Deliverable_draft_is_entitlement_denied_in_community()
    {
        var response = await Client(tenant: "t-deliverable-denied").PostAsJsonAsync(
            "/api/v1/deliverables/draft",
            new DeliverableDraftRequest("deck", ["app"]),
            Json);

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("atlas.ai.generate", body.GetProperty("capability").GetString());
        Assert.Equal("entitlement_denied", body.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task Entitled_deliverable_draft_falls_back_to_a_grounded_template_when_ai_is_not_configured()
    {
        using var entitledFactory = new EntitledDeliverableFactory();
        var author = entitledFactory.CreateClient();
        author.DefaultRequestHeaders.Add("X-Tenant-Id", "t-deliverable-template");
        author.DefaultRequestHeaders.Add("X-Principal-Id", "arch");
        author.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");
        await author.PostAsJsonAsync("/api/v1/assets",
            new Asset("app", AssetKind.Application, "Portal", Lifecycle.Active), Json);

        var client = entitledFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-deliverable-template");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "viewer");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasCustomer");

        var response = await client.PostAsJsonAsync(
            "/api/v1/deliverables/draft",
            new DeliverableDraftRequest("deck", ["app"], Goal: "Board update"),
            Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var draft = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("deck", draft.GetProperty("format").GetString());
        Assert.Equal("template", draft.GetProperty("mode").GetString());
        Assert.Equal("ai_not_configured", draft.GetProperty("status").GetString());
        Assert.Equal("Board update", draft.GetProperty("title").GetString());
        Assert.Contains("Portal", draft.GetProperty("markdown").GetString()!);
        Assert.True(draft.GetProperty("reviewRequired").GetBoolean());

        var audit = entitledFactory.Services.GetRequiredService<InMemoryAuditSink>();
        Assert.Contains(audit.Events, e =>
            e.Action == "atlas.ai.generate" &&
            e.Tenant.TenantId == "t-deliverable-template" &&
            e.Resource.Value.Contains("format=deck", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Entitled_deliverable_draft_uses_the_fabric_ai_contract_when_a_provider_is_configured()
    {
        using var aiFactory = new AvailableAiDeliverableFactory();
        var client = aiFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-deliverable-ai");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "arch");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");

        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("app", AssetKind.Application, "Payments", Lifecycle.Active), Json);

        var draft = await (await client.PostAsJsonAsync(
            "/api/v1/deliverables/draft",
            new DeliverableDraftRequest("doc", ["app"]),
            Json)).Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.Equal("ai", draft.GetProperty("mode").GetString());
        Assert.Equal("available", draft.GetProperty("status").GetString());
        Assert.Equal("ai:test-provider", draft.GetProperty("source").GetString());
        Assert.Contains("Generated architecture brief", draft.GetProperty("markdown").GetString()!);
    }

    private class EntitledDeliverableFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<Vev.Atlas.Persistence.AtlasDbContext>>();
                services.AddDbContext<Vev.Atlas.Persistence.AtlasDbContext>(options => options.UseSqlite(_connection));
                var entitlements = new CommunityEntitlementService(
                    new HashSet<string>(StringComparer.Ordinal) { AtlasCapabilities.AiGenerate.Value });
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

    private sealed class AvailableAiDeliverableFactory : EntitledDeliverableFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAiAssistService>();
                services.AddSingleton<IAiAssistService>(new TestAiAssistService());
            });
        }
    }

    private sealed class TestAiAssistService : IAiAssistService
    {
        public AiAssistResult Assist(AiAssistRequest request) =>
            AiAssistResult.Available(
                "Generated architecture brief\n\n- Grounded in the selected slice\n- Review before export",
                "ai:test-provider");
    }
}
