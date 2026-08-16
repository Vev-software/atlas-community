using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vev.Atlas.Domain;
using Vev.Atlas.Fabric;
using Vev.Atlas.Fabric.Dev;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// Visible AI-allowance tests: the free Community allowance is shown, exhaustion is reason-coded, and
/// a stronger entitlement lifts the limit automatically.
/// </summary>
public sealed class AiAllowanceTests(AtlasApiFactory factory) : IClassFixture<AtlasApiFactory>
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
    public async Task Allowance_endpoint_reports_the_remaining_free_structure_allowance()
    {
        var audit = factory.Services.GetRequiredService<InMemoryAuditSink>();
        await audit.WriteAsync(new AuditEvent(
            TenantId: "t-allowance-limited",
            ActorPrincipalId: "viewer",
            Action: AtlasCapabilities.AiStructure.Value,
            Resource: "atlas:structure-draft",
            OccurredAt: TimeProvider.System.GetUtcNow(),
            CorrelationId: Guid.NewGuid().ToString("N")));

        var response = await Client(tenant: "t-allowance-limited").GetAsync("/api/v1/ai/allowances");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        var capability = body.GetProperty("capabilities")[0];
        Assert.Equal("atlas.ai.structure", capability.GetProperty("capability").GetString());
        Assert.Equal("limited", capability.GetProperty("status").GetString());
        Assert.Equal(3, capability.GetProperty("limit").GetInt32());
        Assert.Equal(1, capability.GetProperty("used").GetInt32());
        Assert.Equal(2, capability.GetProperty("remaining").GetInt32());
        Assert.Equal("day", capability.GetProperty("window").GetString());
    }

    [Fact]
    public async Task Stronger_entitlement_lifts_the_structure_allowance_to_unlimited()
    {
        using var entitledFactory = new UnlimitedStructureAllowanceFactory();
        var client = entitledFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-allowance-unlimited");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "viewer");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasCustomer");

        var response = await client.GetAsync("/api/v1/ai/allowances");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        var capability = body.GetProperty("capabilities")[0];
        Assert.Equal("unlimited", capability.GetProperty("status").GetString());
        Assert.True(capability.GetProperty("unlimited").GetBoolean());
        Assert.Equal("allow", capability.GetProperty("reasonCode").GetString());
    }

    private sealed class UnlimitedStructureAllowanceFactory : WebApplicationFactory<Program>
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
                    new HashSet<string>(StringComparer.Ordinal),
                    new Dictionary<string, EntitlementAllowanceSnapshot>(StringComparer.Ordinal)
                    {
                        [AtlasCapabilities.AiStructure.Value] = EntitlementAllowanceSnapshot.UnlimitedAllowance("entitlement:test")
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
