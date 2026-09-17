using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vev.Atlas.Contracts;
using Vev.Atlas.Domain;
using Vev.Atlas.Fabric;
using Vev.Atlas.Fabric.Dev;
using Xunit;

namespace Vev.Atlas.Api.Tests;

public sealed class TargetSketchTests
{
    private static HttpClient Client(AtlasApiFactory factory, string tenant, bool author = true)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        client.DefaultRequestHeaders.Add("X-Principal-Id", "target-test");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", author ? "AtlasArchitect" : "AtlasCustomer");
        return client;
    }

    private static TargetSketchRequest Addition(string name = "Next") => new(name,
        [new("asset", "future", "planned-add", "Planned replacement",
            new Asset("future", AssetKind.System, "Future system", Lifecycle.Draft))]);

    [Fact]
    public async Task Two_versions_are_saved_third_is_denied_and_other_tenant_is_isolated()
    {
        using var factory = new AtlasApiFactory();
        using var author = Client(factory, "target-a");
        Assert.Equal(HttpStatusCode.Created, (await author.PostAsJsonAsync("/api/v1/targets", Addition())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await author.PostAsJsonAsync("/api/v1/targets", Addition("Next revision"))).StatusCode);
        var denied = await author.PostAsJsonAsync("/api/v1/targets", Addition("Third"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains(AtlasReasonCodes.EntitlementLimitExhausted, await denied.Content.ReadAsStringAsync());
        var snapshot = await author.GetFromJsonAsync<TargetSketchSnapshot>("/api/v1/targets");
        Assert.Equal(2, snapshot!.Versions.Count);
        Assert.Equal(2, snapshot.Allowance.Used);
        Assert.Equal(0, snapshot.Allowance.Remaining);
        Assert.False(snapshot.Allowance.Allowed);
        Assert.Equal(HttpStatusCode.NotFound, (await author.GetAsync("/api/v1/assets/future")).StatusCode);
        using var other = Client(factory, "target-b");
        Assert.Empty((await other.GetFromJsonAsync<TargetSketchSnapshot>("/api/v1/targets"))!.Versions);
        Assert.Equal(HttpStatusCode.Created, (await other.PostAsJsonAsync("/api/v1/targets", Addition())).StatusCode);
        using var viewer = Client(factory, "target-a", false);
        Assert.Equal(2, (await viewer.GetFromJsonAsync<TargetSketchSnapshot>("/api/v1/targets"))!.Versions.Count);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/targets", Addition())).StatusCode);
    }

    [Fact]
    public async Task An_entitled_allowance_can_lift_the_version_cap()
    {
        using var root = new AtlasApiFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEntitlementAllowanceProvider>();
            services.AddSingleton<IEntitlementAllowanceProvider>(new CommunityEntitlementService(
                new HashSet<string> { AtlasCapabilities.TargetVersions.Value }));
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "target-unlimited");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "author");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/targets", Addition())).StatusCode);
        var snapshot = await client.GetFromJsonAsync<TargetSketchSnapshot>("/api/v1/targets");
        Assert.True(snapshot!.Allowance.Unlimited);
        Assert.Equal(3, snapshot.Allowance.Used);
    }

    [Fact]
    public async Task Invalid_intents_do_not_consume_allowance_and_held_facts_are_snapshotted()
    {
        using var factory = new AtlasApiFactory();
        using var client = Client(factory, "target-validation");
        var invalid = new TargetSketchRequest("Bad", [new("asset", "missing", "planned-retire", "Replace")]);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/targets", invalid)).StatusCode);
        await client.PostAsJsonAsync("/api/v1/assets", new Asset("old", AssetKind.System, "Old system", Lifecycle.Retired));
        var request = new TargetSketchRequest("Replace old system", [new("asset", "old", "planned-retire", "Replacement planned")]);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/targets", request)).StatusCode);
        await client.DeleteAsync("/api/v1/assets/old");
        var snapshot = await client.GetFromJsonAsync<TargetSketchSnapshot>("/api/v1/targets");
        Assert.Equal(1, snapshot!.Allowance.Used);
        Assert.Equal("Old system", Assert.Single(Assert.Single(snapshot.Versions).Assets).Name);
    }
}
