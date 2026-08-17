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
/// Draft-structuring tests: free-but-bounded in Community, clean manual fallback with no provider, and
/// multimodal image input routed through the Fabric AI seam.
/// </summary>
public sealed class StructureDraftTests(AtlasApiFactory factory) : IClassFixture<AtlasApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Client(string tenant = "acme", string principal = "viewer", string roles = "AtlasCustomer")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        client.DefaultRequestHeaders.Add("X-Principal-Id", principal);
        client.DefaultRequestHeaders.Add("X-Principal-Roles", roles);
        return client;
    }

    [Fact]
    public async Task Structure_draft_uses_the_free_community_allowance_before_ai_is_configured()
    {
        var response = await Client(tenant: "t-structure-denied").PostAsJsonAsync(
            "/api/v1/structure/draft",
            new StructureDraftRequest("App runs on server"),
            Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var draft = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("manual", draft.GetProperty("mode").GetString());
        Assert.Equal("ai_not_configured", draft.GetProperty("status").GetString());
        Assert.True(draft.GetProperty("reviewRequired").GetBoolean());
    }

    [Fact]
    public async Task Structure_draft_is_denied_when_the_free_allowance_is_exhausted()
    {
        var audit = factory.Services.GetRequiredService<InMemoryAuditSink>();
        for (var i = 0; i < 3; i++)
        {
            await audit.WriteAsync(new AuditEvent(
                EventId: Guid.NewGuid().ToString("N"),
                OccurredAt: TimeProvider.System.GetUtcNow(),
                Tenant: new TenantContext("t-structure-exhausted"),
                Actor: new AuditActor("viewer"),
                Source: "atlas",
                Action: AtlasCapabilities.AiStructure.Value,
                Resource: new AuditResource("atlas:structure-draft"),
                Category: AuditCategory.Data,
                Outcome: AuditOutcome.Success,
                CorrelationId: Guid.NewGuid().ToString("N")));
        }

        var response = await Client(tenant: "t-structure-exhausted").PostAsJsonAsync(
            "/api/v1/structure/draft",
            new StructureDraftRequest("App runs on server"),
            Json);

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("atlas.ai.structure", body.GetProperty("capability").GetString());
        Assert.Equal("entitlement_limit_exhausted", body.GetProperty("reasonCode").GetString());
        Assert.Equal(3, body.GetProperty("limit").GetInt32());
        Assert.Equal(3, body.GetProperty("used").GetInt32());
        Assert.Equal(0, body.GetProperty("remaining").GetInt32());
    }

    [Fact]
    public async Task Entitled_structure_draft_degrades_to_manual_mode_when_ai_is_not_configured()
    {
        using var entitledFactory = new StructureEntitledFactory();
        var client = entitledFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-structure-manual");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "viewer");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasCustomer");

        var response = await client.PostAsJsonAsync(
            "/api/v1/structure/draft",
            new StructureDraftRequest(
                Text: null,
                Images:
                [
                    new StructureDraftImage("whiteboard.png", "image/png", Convert.ToBase64String([1, 2, 3, 4]))
                ]),
            Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var draft = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("manual", draft.GetProperty("mode").GetString());
        Assert.Equal("ai_not_configured", draft.GetProperty("status").GetString());
        Assert.Equal(0, draft.GetProperty("proposal").GetProperty("assets").GetArrayLength());
        Assert.True(draft.GetProperty("reviewRequired").GetBoolean());
        Assert.Contains("Fabric AI provider", draft.GetProperty("guidance").GetString()!);

        var audit = entitledFactory.Services.GetRequiredService<InMemoryAuditSink>();
        Assert.Contains(audit.Events, e =>
            e.Action == "atlas.ai.structure" &&
            e.Tenant.TenantId == "t-structure-manual" &&
            e.Resource.Value.Contains("images=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Entitled_structure_draft_returns_an_import_bundle_from_multimodal_ai()
    {
        using var aiFactory = new StructureAiFactory();
        var client = aiFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-structure-ai");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "arch");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");

        var response = await client.PostAsJsonAsync(
            "/api/v1/structure/draft",
            new StructureDraftRequest(
                "Checkout app on srv-01",
                [new StructureDraftImage("diagram.png", "image/png", Convert.ToBase64String([9, 8, 7]))]),
            Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var draft = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("ai", draft.GetProperty("mode").GetString());
        Assert.Equal("available", draft.GetProperty("status").GetString());
        Assert.Equal("ai:test-multimodal", draft.GetProperty("source").GetString());
        Assert.Equal(2, draft.GetProperty("proposal").GetProperty("assets").GetArrayLength());
        Assert.Single(draft.GetProperty("proposal").GetProperty("relationships").EnumerateArray());
    }

    private class StructureEntitledFactory : WebApplicationFactory<Program>
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
                    new HashSet<string>(StringComparer.Ordinal) { AtlasCapabilities.AiStructure.Value });
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

    private sealed class StructureAiFactory : StructureEntitledFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAiAssistService>();
                services.AddSingleton<IAiAssistService>(new TestStructureAiAssistService());
            });
        }
    }

    private sealed class TestStructureAiAssistService : IAiAssistService
    {
        public AiAssistResult Assist(AiAssistRequest request)
        {
            Assert.NotNull(request.Attachments);
            Assert.Single(request.Attachments!);

            var bundle = new ImportBundle(
                Assets:
                [
                    new ImportAsset(AssetKind.Application, "Checkout", Lifecycle.Draft, Id: "app-checkout"),
                    new ImportAsset(AssetKind.Server, "srv-01", Lifecycle.Draft, Id: "srv-01"),
                ],
                Relationships:
                [
                    new ImportRelationship("app-checkout", "srv-01", RelationshipType.RunsOn, Id: "r1")
                ],
                Mode: ImportMode.Merge);

            var json = JsonSerializer.Serialize(bundle, AtlasContracts.SerializerOptions);
            return AiAssistResult.Available(json, "ai:test-multimodal");
        }
    }
}
