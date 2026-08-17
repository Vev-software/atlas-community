using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vev.Atlas.Contracts;
using Xunit;

namespace Vev.Atlas.Api.Tests;

public sealed class ShadowItTests(AtlasApiFactory factory) : IClassFixture<AtlasApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Client(string tenant = "t-shadow-it")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        client.DefaultRequestHeaders.Add("X-Principal-Id", "arch");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");
        return client;
    }

    [Fact]
    public async Task Summary_with_empty_catalogue_returns_all_zeros()
    {
        var client = Client("t-empty");
        var resp = await client.GetFromJsonAsync<ShadowItSummaryResponse>("/api/v1/shadow-it/summary", Json);

        Assert.NotNull(resp);
        Assert.Equal(0, resp!.TotalAssets);
        Assert.Equal(0, resp.OwnedAssets);
        Assert.Equal(0, resp.UnownedAssets);
        Assert.Equal(0, resp.OwnershipCoveragePercent);
        Assert.Equal(0, resp.SanctionedAssets);
        Assert.Equal(0, resp.UnsanctionedAssets);
        Assert.Equal(0, resp.ActiveAssets);
        Assert.Equal(0, resp.RetiredAssets);
    }

    [Fact]
    public async Task Summary_computes_ownership_and_sanctioned_counts_correctly()
    {
        var client = Client("t-summary-mixed");

        // Owned + sanctioned application
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("app-1", AssetKind.Application, "Checkout", Lifecycle.Active,
                Application: new ApplicationDetails(BusinessOwner: "CTO"),
                Tags: [new Tag("sanctioned", "true")]), Json);

        // Unowned + unsanctioned server
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("srv-1", AssetKind.Server, "Legacy server", Lifecycle.Active), Json);

        // Owned via dataset owner + sanctioned
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("ds-1", AssetKind.Dataset, "Customers", Lifecycle.Active,
                Dataset: new DatasetDetails(PhysicalName: "dbo.customers", Owner: "CRM team"),
                Tags: [new Tag("sanctioned", "true")]), Json);

        // Owned via tag + unsanctioned + retired
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("infra-1", AssetKind.Infrastructure, "Old network", Lifecycle.Retired,
                Tags: [new Tag("owner", "IT-ops")]), Json);

        var resp = await client.GetFromJsonAsync<ShadowItSummaryResponse>("/api/v1/shadow-it/summary", Json);

        Assert.NotNull(resp);
        Assert.Equal(4, resp!.TotalAssets);
        Assert.Equal(3, resp.OwnedAssets);
        Assert.Equal(1, resp.UnownedAssets);
        Assert.Equal(75, resp.OwnershipCoveragePercent);
        Assert.Equal(2, resp.SanctionedAssets);
        Assert.Equal(2, resp.UnsanctionedAssets);
        Assert.Equal(3, resp.ActiveAssets);
        Assert.Equal(1, resp.RetiredAssets);
    }

    [Fact]
    public async Task Filter_unowned_returns_only_assets_without_ownership()
    {
        var client = Client("t-filter-unowned");

        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("app-owned", AssetKind.Application, "Owned App", Lifecycle.Active,
                Application: new ApplicationDetails(BusinessOwner: "CTO")), Json);

        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("app-unowned", AssetKind.Application, "Unowned App", Lifecycle.Active), Json);

        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("srv-unowned", AssetKind.Server, "No owner server", Lifecycle.Active), Json);

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/v1/shadow-it/assets?unowned=true", Json);
        var ids = resp!.EnumerateArray()
            .Select(a => a.GetProperty("id").GetString()).ToList();

        Assert.Equal(2, ids.Count);
        Assert.Contains("app-unowned", ids);
        Assert.Contains("srv-unowned", ids);
    }

    [Fact]
    public async Task Filter_unsanctioned_returns_assets_without_sanctioned_tag()
    {
        var client = Client("t-filter-unsanctioned");

        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("app-sanctioned", AssetKind.Application, "Sanctioned", Lifecycle.Active,
                Tags: [new Tag("sanctioned", "true")]), Json);

        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("app-unsanctioned", AssetKind.Application, "Unsanctioned", Lifecycle.Active), Json);

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/v1/shadow-it/assets?unsanctioned=true", Json);
        var ids = resp!.EnumerateArray()
            .Select(a => a.GetProperty("id").GetString()).ToList();

        Assert.Single(ids);
        Assert.Equal("app-unsanctioned", ids[0]);
    }

    [Fact]
    public async Task Filter_past_eol_returns_only_retired_assets()
    {
        var client = Client("t-filter-eol");

        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("app-active", AssetKind.Application, "Active", Lifecycle.Active), Json);

        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("app-retired", AssetKind.Application, "Retired", Lifecycle.Retired), Json);

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/v1/shadow-it/assets?pastEol=true", Json);
        var ids = resp!.EnumerateArray()
            .Select(a => a.GetProperty("id").GetString()).ToList();

        Assert.Single(ids);
        Assert.Equal("app-retired", ids[0]);
    }

    [Fact]
    public async Task Combined_filter_uses_or_logic()
    {
        var client = Client("t-filter-or");

        // Unowned but active and sanctioned
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("a1", AssetKind.System, "Unowned", Lifecycle.Active,
                Tags: [new Tag("sanctioned", "true")]), Json);

        // Owned but unsanctioned
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("a2", AssetKind.Application, "Unsanctioned", Lifecycle.Active,
                Application: new ApplicationDetails(BusinessOwner: "CTO")), Json);

        // Owned + sanctioned but retired
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("a3", AssetKind.Application, "Retired", Lifecycle.Retired,
                Application: new ApplicationDetails(BusinessOwner: "CTO"),
                Tags: [new Tag("sanctioned", "true")]), Json);

        // Clean asset — owned, sanctioned, active
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("a4", AssetKind.Application, "Clean", Lifecycle.Active,
                Application: new ApplicationDetails(BusinessOwner: "CTO"),
                Tags: [new Tag("sanctioned", "true")]), Json);

        var resp = await client.GetFromJsonAsync<JsonElement>(
            "/api/v1/shadow-it/assets?unowned=true&unsanctioned=true&pastEol=true", Json);
        var ids = resp!.EnumerateArray()
            .Select(a => a.GetProperty("id").GetString()).ToList();

        Assert.Equal(3, ids.Count);
        Assert.Contains("a1", ids);
        Assert.Contains("a2", ids);
        Assert.Contains("a3", ids);
        Assert.DoesNotContain("a4", ids);
    }

    [Fact]
    public async Task Ownership_detection_for_application_business_owner()
    {
        var client = Client("t-app-owner");

        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("app-bo", AssetKind.Application, "With BO", Lifecycle.Active,
                Application: new ApplicationDetails(BusinessOwner: "VP Engineering")), Json);

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/v1/shadow-it/assets?unowned=true", Json);
        var ids = resp!.EnumerateArray()
            .Select(a => a.GetProperty("id").GetString()).ToList();

        Assert.DoesNotContain("app-bo", ids);
    }

    [Fact]
    public async Task Ownership_detection_for_dataset_owner()
    {
        var client = Client("t-ds-owner");

        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("ds-owned", AssetKind.Dataset, "Owned Dataset", Lifecycle.Active,
                Dataset: new DatasetDetails(PhysicalName: "dbo.owned", Owner: "Data team")), Json);

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/v1/shadow-it/assets?unowned=true", Json);
        var ids = resp!.EnumerateArray()
            .Select(a => a.GetProperty("id").GetString()).ToList();

        Assert.DoesNotContain("ds-owned", ids);
    }

    [Fact]
    public async Task Ownership_detection_via_tag()
    {
        var client = Client("t-tag-owner");

        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("srv-tagged", AssetKind.Server, "Tagged Owner", Lifecycle.Active,
                Tags: [new Tag("owner", "IT-team")]), Json);

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/v1/shadow-it/assets?unowned=true", Json);
        var ids = resp!.EnumerateArray()
            .Select(a => a.GetProperty("id").GetString()).ToList();

        Assert.DoesNotContain("srv-tagged", ids);
    }

    [Fact]
    public async Task Read_only_principal_can_access_shadow_it_summary()
    {
        var client = ClientRo("t-ro");
        var resp = await client.GetAsync("/api/v1/shadow-it/summary");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Read_only_principal_can_access_shadow_it_assets()
    {
        var client = ClientRo("t-ro-assets");
        var resp = await client.GetAsync("/api/v1/shadow-it/assets?unowned=true");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private HttpClient ClientRo(string tenant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        client.DefaultRequestHeaders.Add("X-Principal-Id", "customer");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasCustomer");
        return client;
    }

    private sealed class ShadowItSummaryResponse(
        int TotalAssets, int OwnedAssets, int UnownedAssets, double OwnershipCoveragePercent,
        int SanctionedAssets, int UnsanctionedAssets, int ActiveAssets, int RetiredAssets)
    {
        public int TotalAssets { get; } = TotalAssets;
        public int OwnedAssets { get; } = OwnedAssets;
        public int UnownedAssets { get; } = UnownedAssets;
        public double OwnershipCoveragePercent { get; } = OwnershipCoveragePercent;
        public int SanctionedAssets { get; } = SanctionedAssets;
        public int UnsanctionedAssets { get; } = UnsanctionedAssets;
        public int ActiveAssets { get; } = ActiveAssets;
        public int RetiredAssets { get; } = RetiredAssets;
    }
}
