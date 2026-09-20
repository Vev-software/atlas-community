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
    [Fact]
    public async Task Same_origin_fragments_receive_bearer_credentials_inside_an_unchanged_sandbox()
    {
        await using var context = await _browser!.NewContextAsync();
        await context.AddInitScriptAsync("if (!location.pathname.startsWith('/login')) sessionStorage.setItem('atlas_token', 'synthetic-browser-token')");
        var page = await context.NewPageAsync();
        string? authorization = null;
        await page.RouteAsync("**/api/v1/extensions/ui", route => route.FulfillAsync(new()
        {
            ContentType = "application/json",
            Body = """{"contractVersion":"1","extensions":[{"kind":"ui-extension","id":"test","slot":"landscape-right-rail","mount":{"kind":"fragment","contractVersion":"1","url":"/test-fragment"}}]}"""
        }));
        await page.RouteAsync("**/test-fragment", async route =>
        {
            authorization = (await route.Request.AllHeadersAsync()).GetValueOrDefault("authorization");
            await route.FulfillAsync(new() { ContentType = "text/html", Body = "<p>Authenticated fragment</p><script>document.body.textContent='unsafe';</script>" });
        });
        await page.GotoAsync(host.RootUri.ToString());
        await page.FrameLocator(".ext-frame").GetByText("Authenticated fragment").WaitForAsync();
        Assert.Equal("Bearer synthetic-browser-token", authorization);
        Assert.Equal("", await page.Locator(".ext-frame").GetAttributeAsync("sandbox"));
        await page.Locator("#signOut").ClickAsync();
        await page.Locator("#username").WaitForAsync();
        Assert.Null(await page.EvaluateAsync<string?>("sessionStorage.getItem('atlas_token')"));
    }

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
    public async Task Document_upload_can_be_reviewed_edited_and_explicitly_imported()
    {
        await using var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.RootUri.ToString(),
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["X-Tenant-Id"] = "t-document-browser",
                ["X-Principal-Id"] = "author",
                ["X-Principal-Roles"] = "AtlasArchitect"
            }
        });
        var page = await context.NewPageAsync();
        string? submitted = null;
        await page.RouteAsync("**/api/v1/structure/draft", route =>
        {
            submitted = route.Request.PostData;
            return route.FulfillAsync(new()
            {
                ContentType = "application/json",
                Body = """{"mode":"ai","status":"available","source":"ai:test","summary":"Review the document draft","reviewRequired":true,"proposal":{"assets":[{"id":"document-app","name":"From document","kind":"application","lifecycle":"draft"}],"relationships":[],"mode":"merge"}}"""
            });
        });
        await page.GotoAsync("/");
        await page.Locator("#pasteLandscape").ClickAsync();
        await page.Locator("#pasteImages").SetInputFilesAsync(new FilePayload
        { Name = "inventory.csv", MimeType = "", Buffer = System.Text.Encoding.UTF8.GetBytes("name,kind\nFrom document,application") });
        await page.WaitForSelectorAsync("#pasteImageList .crumb");
        await page.Locator("#pasteGenerate").ClickAsync();
        await page.WaitForSelectorAsync("#draftBackdrop:not([hidden])");
        using var payload = System.Text.Json.JsonDocument.Parse(submitted!);
        Assert.Equal("text/csv", payload.RootElement.GetProperty("documents")[0].GetProperty("contentType").GetString());
        using var client = host.CreateBrowserClient("t-document-browser");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/assets/document-app")).StatusCode);
        await page.GetByLabel("Asset name", new() { Exact = true }).FillAsync("Reviewed document app");
        await page.Locator("#draftImport").ClickAsync();
        await page.WaitForSelectorAsync("#draftBackdrop[hidden]", new() { State = WaitForSelectorState.Attached });
        Assert.Contains("Reviewed document app", await (await client.GetAsync("/api/v1/assets/document-app")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Structure_endpoint_rejects_oversized_http_body_before_binding()
    {
        using var client = host.CreateBrowserClient("t-document-oversize");
        // Wait for the header-only rejection instead of racing an 8 MiB upload against
        // Kestrel closing the connection (Linux reports that race as a broken pipe).
        client.DefaultRequestHeaders.ExpectContinue = true;
        using var content = new StringContent(new string(' ', 8 * 1024 * 1024 + 1), System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/api/v1/structure/draft", content)).StatusCode);
    }

    [Fact]
    public async Task Usage_collection_requires_consent_and_sends_only_allowlisted_fields()
    {
        using var author = host.CreateBrowserClient(tenant: "t-private-usage");
        await author.PostAsJsonAsync("/api/v1/assets", new Asset("private-asset-id", AssetKind.System, "Private landscape name", Lifecycle.Active));
        await using var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.RootUri.ToString(),
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["X-Tenant-Id"] = "t-private-usage",
                ["X-Principal-Id"] = "private-person",
                ["X-Principal-Roles"] = "AtlasCustomer"
            }
        });
        await context.AddCookiesAsync([new Microsoft.Playwright.Cookie { Name = "private-cookie", Value = "secret", Domain = "collector.example", Path = "/", Secure = true }]);
        await context.AddInitScriptAsync("sessionStorage.setItem('atlas_token', 'private-token')");
        var page = await context.NewPageAsync();
        var received = new System.Collections.Concurrent.ConcurrentQueue<IRequest>();
        await page.RouteAsync("**/app-config.js", route => route.FulfillAsync(new()
        {
            ContentType = "application/javascript",
            Body = "window.__ATLAS__={apiBase:'/api',loginPath:'/login',usageAnalytics:{endpoint:'https://collector.example/events'}};"
        }));
        await page.RouteAsync("https://collector.example/events", route =>
        {
            if (route.Request.Method == "POST") received.Enqueue(route.Request);
            return route.FulfillAsync(new()
            {
                Status = 204,
                Headers = new Dictionary<string, string>
                {
                    ["Access-Control-Allow-Origin"] = "*",
                    ["Access-Control-Allow-Methods"] = "POST, OPTIONS",
                    ["Access-Control-Allow-Headers"] = "content-type"
                }
            });
        });
        await page.GotoAsync("/");
        await page.WaitForSelectorAsync("#usageConsent[open]");
        Assert.Equal("usageDecline", await page.EvaluateAsync<string>("document.activeElement.id"));
        await page.Keyboard.PressAsync("Escape");
        await page.GetByTitle("Table view").ClickAsync();
        await page.Locator(".asset-table tbody tr").ClickAsync();
        await page.WaitForTimeoutAsync(700);
        Assert.Empty(received);
        await page.Locator("#usagePrivacy").ClickAsync();
        await page.Locator("#usageAccept").ClickAsync();
        await page.GetByTitle("Graph view").ClickAsync();
        await page.Locator("#search").FillAsync("Private search text");
        await page.Locator("#canvas .node").ClickAsync();
        await page.WaitForTimeoutAsync(800);
        Assert.NotEmpty(received);
        foreach (var request in received)
        {
            var body = request.PostData!;
            Assert.DoesNotContain("private", body, StringComparison.OrdinalIgnoreCase);
            var headers = await request.AllHeadersAsync();
            Assert.False(headers.ContainsKey("authorization"));
            Assert.False(headers.ContainsKey("cookie"));
            Assert.False(headers.ContainsKey("referer"));
            using var json = System.Text.Json.JsonDocument.Parse(body);
            foreach (var item in json.RootElement.GetProperty("events").EnumerateArray())
                Assert.Equal(new[] { "cell", "control", "elapsed", "from", "step", "to", "view" },
                    item.EnumerateObject().Select(p => p.Name).Order().ToArray());
        }
        await page.Locator("#usagePrivacy").ClickAsync();
        await page.Locator("#usageDashboard summary").ClickAsync();
        Assert.Equal(24, await page.Locator("#usageGrid span").CountAsync());
        Assert.Contains("asset-open", await page.Locator("#usageCounts").InnerTextAsync());
        await page.Locator("#usageWithdraw").ClickAsync();
        await page.Locator("#usageClose").ClickAsync();
        var sent = received.Count;
        await page.GetByTitle("Table view").ClickAsync();
        await page.WaitForTimeoutAsync(700);
        Assert.Equal(sent, received.Count);
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
    public async Task Concurrent_target_saves_cannot_exceed_the_allowance()
    {
        using var author = host.CreateBrowserClient(tenant: "t-target-race");
        var request = new Vev.Atlas.Domain.TargetSketchRequest("Next", [new("asset", "new-system", "planned-add", "Planned addition",
            new Asset("new-system", AssetKind.System, "New system", Lifecycle.Draft))]);
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => author.PostAsJsonAsync("/api/v1/targets", request)));
        Assert.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Forbidden));
    }

    [Fact]
    public async Task Retired_assets_seed_a_saved_read_only_target_overlay()
    {
        using var author = host.CreateBrowserClient(tenant: "t-ui-target");
        await author.PostAsJsonAsync("/api/v1/assets", new Asset("retired-system", AssetKind.System, "Retired system", Lifecycle.Retired));
        await using var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.RootUri.ToString(),
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["X-Tenant-Id"] = "t-ui-target",
                ["X-Principal-Id"] = "author",
                ["X-Principal-Roles"] = "AtlasArchitect"
            }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/");
        await page.Locator("#targetSeed").ClickAsync();
        await page.WaitForSelectorAsync("#canvas .node.planned-retire");
        Assert.Contains("1 / 2", await page.Locator("#targetAllowance").InnerTextAsync());
        await page.Locator("#canvas .node.planned-retire").ClickAsync();
        Assert.Contains("Plan replacement", await page.Locator("#detail").InnerTextAsync());
        Assert.Equal(0, await page.Locator("#detail button").CountAsync());
        await page.Locator("#targetAsIs").ClickAsync();
        Assert.Equal(0, await page.Locator("#canvas .planned-retire").CountAsync());
        await page.ReloadAsync();
        await page.Locator("#targetToBe").ClickAsync();
        await page.WaitForSelectorAsync("#canvas .node.planned-retire");
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
