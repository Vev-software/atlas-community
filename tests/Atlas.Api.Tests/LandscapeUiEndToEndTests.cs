using System.Net;
using System.Net.Http.Json;
using Microsoft.Playwright;
using Vev.Atlas.Contracts;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// Browser-level proof for atlas-community#134: the shipped UI can load the landscape, switch view,
/// select an asset and drive the column search flow against the real API.
/// </summary>
public sealed class LandscapeUiEndToEndTests(AtlasUiTestHost host) : IClassFixture<AtlasUiTestHost>, IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }

    [Fact]
    public async Task Named_views_group_tags_and_fail_closed_on_extension_changes()
    {
        await using var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.RootUri.ToString(),
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["X-Tenant-Id"] = "t-ui-shell",
                ["X-Principal-Id"] = "viewer",
                ["X-Principal-Roles"] = "AtlasCustomer"
            }
        });
        var page = await context.NewPageAsync();
        var entitled = true;
        await page.RouteAsync("**/api/v1/entitlements/summary", route => route.FulfillAsync(new()
        {
            ContentType = "application/json",
            Body = "{\"capabilities\":[{\"capability\":\"atlas.analysis.roadmap\",\"enabled\":" + (entitled ? "true" : "false") + "}]}"
        }));
        await page.RouteAsync("**/api/v1/extensions/ui", route => route.FulfillAsync(new()
        {
            ContentType = "application/json",
            Body = """{"contractVersion":"2","extensions":[{"kind":"ui-extension","id":"test-roadmap","slot":"view-roadmaps","mount":{"kind":"fragment","contractVersion":"1","url":"/api/v1/extensions/roadmaps"}}]}"""
        }));
        await page.RouteAsync("**/api/v1/extensions/roadmaps", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = "<h1>Entitled roadmap fragment</h1>"
        }));
        await page.GotoAsync("/#roadmaps");
        await page.WaitForSelectorAsync("#ext-slot-view-roadmaps iframe");
        Assert.Equal("", await page.Locator("#ext-slot-view-roadmaps iframe").GetAttributeAsync("sandbox"));
        Assert.False(await page.Locator("#toolbar").IsVisibleAsync());
        Assert.False(await page.Locator("#detailRail").IsVisibleAsync());
        entitled = false;
        await page.EvaluateAsync("closeAccountPanel()");
        await page.WaitForSelectorAsync("#viewTeaser button");
        Assert.Equal(0, await page.Locator("#ext-slot-view-roadmaps iframe").CountAsync());
        await page.GetByRole(AriaRole.Button, new() { Name = "Capabilities", Exact = true }).ClickAsync();
        Assert.Contains("capability:<name>", await page.Locator("#capabilitiesView").InnerTextAsync());
        await page.EvaluateAsync("""() => { landscape.assets = [{id:'s1', name:'Tagged system', kind:'system', tags:[{key:'capability',value:'Billing'},{key:'capability',value:'Billing'}]}, {id:'s2',name:'Other system',kind:'system'}]; render(); }""");
        await page.GetByRole(AriaRole.Button, new() { Name = "Billing 1 systems" }).ClickAsync();
        Assert.EndsWith("#systems?capability=Billing", page.Url);
        Assert.Equal(1, await page.Locator(".asset-table tbody tr").CountAsync());
        Assert.Contains("Tagged system", await page.Locator(".asset-table tbody").InnerTextAsync());
    }

    [Fact]
    public async Task Read_only_user_can_navigate_the_landscape_in_the_browser()
    {
        using (var author = host.CreateBrowserClient(tenant: "t-ui-nav"))
        {
            var responses = new[]
            {
                await author.PostAsJsonAsync("/api/v1/assets",
                    new Asset("sys-crm", AssetKind.System, "CRM platform", Lifecycle.Active)),
                await author.PostAsJsonAsync("/api/v1/assets",
                    new Asset("da-customer", AssetKind.DataArea, "Customer data", Lifecycle.Active,
                        DataArea: new DataAreaDetails("microservice"))),
                await author.PostAsJsonAsync("/api/v1/assets",
                    new Asset("ds-customers", AssetKind.Dataset, "Customers", Lifecycle.Active,
                        Dataset: new DatasetDetails(PhysicalName: "dbo.customers", Owner: "CRM team"))),
                await author.PostAsJsonAsync("/api/v1/assets",
                    new Asset("col-customer-id", AssetKind.Column, "customer_id", Lifecycle.Active,
                        Column: new ColumnDetails(DataType: "uuid", Nullable: false)))
            };

            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));

            var relationshipResponses = new[]
            {
                await author.PostAsJsonAsync("/api/v1/relationships",
                    new Relationship("r-da", "da-customer", "sys-crm", RelationshipType.PartOf)),
                await author.PostAsJsonAsync("/api/v1/relationships",
                    new Relationship("r-ds", "ds-customers", "da-customer", RelationshipType.PartOf)),
                await author.PostAsJsonAsync("/api/v1/relationships",
                    new Relationship("r-col", "col-customer-id", "ds-customers", RelationshipType.PartOf))
            };

            Assert.All(relationshipResponses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        }

        await using var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.RootUri.ToString(),
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["X-Tenant-Id"] = "t-ui-nav",
                ["X-Principal-Id"] = "viewer",
                ["X-Principal-Roles"] = "AtlasCustomer"
            }
        });

        var page = await context.NewPageAsync();
        await page.GotoAsync("/");

        await page.WaitForSelectorAsync("#toolbar:not([hidden])");
        await page.WaitForSelectorAsync("#canvas svg");

        Assert.Equal("Atlas · Community", (await page.Locator("#brandName").TextContentAsync())?.Trim());
        Assert.Equal("Read-only", (await page.Locator("#capBadge").TextContentAsync())?.Trim());
        Assert.Equal("4 of 4 assets · 3 relationships", (await page.Locator("#count").TextContentAsync())?.Trim());

        await page.GetByTitle("Table view").ClickAsync();
        await page.WaitForSelectorAsync("table.asset-table");
        await page.Locator("table.asset-table tbody tr").Filter(new() { HasTextString = "Customers" }).ClickAsync();

        var detail = page.Locator("#detail");
        await detail.WaitForAsync();
        Assert.Contains("Customers", await detail.InnerTextAsync());
        Assert.Contains("dataset", await detail.InnerTextAsync());
        Assert.Contains("dbo.customers", await detail.InnerTextAsync());

        await page.Locator("#search").FillAsync("customer_id");
        await page.Locator("#detail .search-card").GetByText("customer_id", new() { Exact = true }).ClickAsync();

        var selectedDetail = await page.Locator("#detail").InnerTextAsync();
        Assert.Contains("customer_id", selectedDetail);
        Assert.Contains("Column", selectedDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CRM platform / Customer data / Customers / customer_id", selectedDetail);
    }
}
