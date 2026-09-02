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
