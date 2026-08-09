using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vev.Atlas.Contracts;
using Vev.Atlas.Fabric.Dev;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>End-to-end tests for the Community catalogue: CRUD, relationships, tenant isolation, authz and the paid seam.</summary>
public sealed class CatalogueApiTests(AtlasApiFactory factory) : IClassFixture<AtlasApiFactory>
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

    private static Asset SampleApp(string id = "app-checkout") =>
        new(id, AssetKind.Application, "Checkout", Lifecycle.Active,
            Tags: [new Tag("tier", "critical")],
            Application: new ApplicationDetails(Version: "1.0.0", Vendor: "in-house"));

    [Fact]
    public async Task Create_then_get_asset_round_trips_through_the_stack()
    {
        var client = Client(tenant: "t-roundtrip");

        var create = await client.PostAsJsonAsync("/api/v1/assets", SampleApp(), Json);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var fetched = await client.GetFromJsonAsync<Asset>("/api/v1/assets/app-checkout", Json);
        Assert.NotNull(fetched);
        Assert.Equal("Checkout", fetched!.Name);
        Assert.Equal(AssetKind.Application, fetched.Kind);
        Assert.Equal("1.0.0", fetched.Application?.Version);
        Assert.Contains(fetched.Tags, t => t is { Key: "tier", Value: "critical" });
    }

    [Fact]
    public async Task Listing_can_filter_by_kind()
    {
        var client = Client(tenant: "t-filter");
        await client.PostAsJsonAsync("/api/v1/assets", SampleApp("a1"), Json);
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("s1", AssetKind.Server, "srv-01", Lifecycle.Active), Json);

        var servers = await client.GetFromJsonAsync<List<Asset>>("/api/v1/assets?kind=server", Json);

        Assert.NotNull(servers);
        Assert.Single(servers!);
        Assert.Equal(AssetKind.Server, servers![0].Kind);
    }

    [Fact]
    public async Task Manual_relationship_requires_both_endpoints_to_exist()
    {
        var client = Client(tenant: "t-rel");
        await client.PostAsJsonAsync("/api/v1/assets", SampleApp("app"), Json);
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("srv", AssetKind.Server, "srv", Lifecycle.Active), Json);

        var ok = await client.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r1", "app", "srv", RelationshipType.RunsOn), Json);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        var missing = await client.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r2", "app", "ghost", RelationshipType.RunsOn), Json);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task Deleting_an_asset_removes_its_relationships()
    {
        var client = Client(tenant: "t-cascade");
        await client.PostAsJsonAsync("/api/v1/assets", SampleApp("app"), Json);
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("srv", AssetKind.Server, "srv-01", Lifecycle.Active), Json);
        await client.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r1", "app", "srv", RelationshipType.RunsOn), Json);

        var deleted = await client.DeleteAsync("/api/v1/assets/srv");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var landscape = await client.GetFromJsonAsync<LandscapeDocument>("/api/v1/landscape", Json);
        Assert.NotNull(landscape);
        Assert.DoesNotContain(landscape!.Assets, a => a.Id == "srv");
        Assert.Empty(landscape.Relationships);

        var audit = factory.Services.GetRequiredService<InMemoryAuditSink>();
        Assert.Contains(audit.Events, e =>
            e.Action == "atlas.relationship.deleted" &&
            e.TenantId == "t-cascade" &&
            e.Resource == "atlas:relationship/r1");
    }

    [Fact]
    public async Task Assets_are_isolated_by_tenant()
    {
        await Client(tenant: "tenant-a").PostAsJsonAsync("/api/v1/assets", SampleApp("shared-id"), Json);

        var otherTenant = await Client(tenant: "tenant-b").GetFromJsonAsync<List<Asset>>("/api/v1/assets", Json);

        Assert.NotNull(otherTenant);
        Assert.Empty(otherTenant!);
    }

    [Fact]
    public async Task A_read_only_customer_cannot_write()
    {
        var readOnly = Client(tenant: "t-authz", principal: "viewer", roles: "AtlasCustomer");

        var response = await readOnly.PostAsJsonAsync("/api/v1/assets", SampleApp("nope"), Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("role_missing", problem.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task Paid_capability_is_entitlement_denied_in_community()
    {
        var client = Client(tenant: "t-paid");
        await client.PostAsJsonAsync("/api/v1/assets", SampleApp("app-paid"), Json);

        var response = await client.GetAsync("/api/v1/assets/app-paid/integration-mapping");

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("entitlement_denied", body.GetProperty("reasonCode").GetString());
        Assert.Equal("atlas.integration.mapping", body.GetProperty("capability").GetString());
    }

    [Fact]
    public async Task Landscape_read_composes_assets_and_relationships_for_the_tenant()
    {
        var client = Client(tenant: "t-landscape");
        await client.PostAsJsonAsync("/api/v1/assets", SampleApp("app"), Json);
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("srv", AssetKind.Server, "srv-01", Lifecycle.Active), Json);
        await client.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r1", "app", "srv", RelationshipType.RunsOn), Json);

        var landscape = await client.GetFromJsonAsync<LandscapeDocument>("/api/v1/landscape", Json);

        Assert.NotNull(landscape);
        Assert.Equal(2, landscape!.Assets.Length);
        Assert.Single(landscape.Relationships);
        Assert.Equal("app", landscape.Relationships[0].FromId);
        Assert.NotNull(landscape.ExportedAt);
    }

    [Fact]
    public async Task Landscape_read_is_isolated_by_tenant()
    {
        await Client(tenant: "t-ls-a").PostAsJsonAsync("/api/v1/assets", SampleApp("only-a"), Json);

        var other = await Client(tenant: "t-ls-b").GetFromJsonAsync<LandscapeDocument>("/api/v1/landscape", Json);

        Assert.NotNull(other);
        Assert.Empty(other!.Assets);
    }

    [Fact]
    public async Task Landscape_visualisation_page_is_served_at_the_root()
    {
        var response = await factory.CreateClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("/api/v1/landscape", body);
    }

    [Fact]
    public async Task Capabilities_report_author_for_an_architect()
    {
        var caps = await Client(roles: "AtlasArchitect")
            .GetFromJsonAsync<JsonElement>("/api/v1/capabilities", Json);

        Assert.True(caps.GetProperty("canAuthor").GetBoolean());
    }

    [Fact]
    public async Task Capabilities_report_read_only_for_a_customer()
    {
        var caps = await Client(principal: "viewer", roles: "AtlasCustomer")
            .GetFromJsonAsync<JsonElement>("/api/v1/capabilities", Json);

        Assert.False(caps.GetProperty("canAuthor").GetBoolean());
    }

    [Fact]
    public async Task Mutations_emit_audit_events()
    {
        var client = Client(tenant: "t-audit");
        await client.PostAsJsonAsync("/api/v1/assets", SampleApp("app-audit"), Json);

        var audit = factory.Services.GetRequiredService<InMemoryAuditSink>();

        Assert.Contains(audit.Events, e =>
            e.Action == "atlas.asset.created" &&
            e.TenantId == "t-audit" &&
            e.Resource == "atlas:asset/app-audit");
    }
}
