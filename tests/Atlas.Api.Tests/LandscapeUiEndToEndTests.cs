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
    public async Task Anonymous_oidc_visit_goes_directly_to_login_without_racing_api_challenges()
    {
        await using var context = await _browser!.NewContextAsync();
        var page = await context.NewPageAsync();
        var apiRequests = 0;
        page.Request += (_, request) => { if (new Uri(request.Url).AbsolutePath.StartsWith("/api/", StringComparison.Ordinal)) Interlocked.Increment(ref apiRequests); };
        await page.RouteAsync("**/app-config.js", route => route.FulfillAsync(new()
        {
            ContentType = "application/javascript",
            Body = "window.__ATLAS__={apiBase:'/api',loginPath:'/login',oidcAuthority:'https://id.example/realms/test'};"
        }));
        await page.GotoAsync(host.RootUri.ToString());
        await page.Locator("#username").WaitForAsync();
        Assert.Equal(0, apiRequests);
    }

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
    public async Task Share_summary_previews_the_scope_and_downloads_a_signed_digest()
    {
        using var client = host.CreateBrowserClient("t-share-browser");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("share-app", AssetKind.Application, "Shared app", Lifecycle.Active, Application: new ApplicationDetails(Vendor: "Example Corp")))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("share-ds", AssetKind.Dataset, "Hidden dataset", Lifecycle.Active))).StatusCode);
        await using var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.RootUri.ToString(),
            AcceptDownloads = true,
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["X-Tenant-Id"] = "t-share-browser",
                ["X-Principal-Id"] = "author",
                ["X-Principal-Roles"] = "AtlasArchitect"
            }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/");
        await page.Locator("#shareDigest:not([hidden])").WaitForAsync();
        await page.Locator("#shareDigest").ClickAsync();

        // The preview lists what would leave the installation and what never can.
        await page.Locator("#shareCount", new() { HasTextString = "2 items will be shared" }).WaitForAsync();
        var preview = await page.Locator("#sharePreview").InnerTextAsync();
        Assert.Contains("Shared app (application, active, Example Corp)", preview);
        Assert.Contains("Example Corp (vendor, active)", preview);
        Assert.DoesNotContain("Hidden dataset", preview);
        Assert.Contains("hostnames", await page.Locator("#shareNever").InnerTextAsync());

        // Narrowing the scope updates the preview.
        await page.Locator("#shareKinds input[value=vendor]").UncheckAsync();
        await page.Locator("#shareCount", new() { HasTextString = "1 item will be shared" }).WaitForAsync();

        var download = await page.RunAndWaitForDownloadAsync(() => page.Locator("#shareDownload").ClickAsync());
        Assert.Equal("atlas-landscape-digest.json", download.SuggestedFilename);
        using var file = System.Text.Json.JsonDocument.Parse(await System.IO.File.ReadAllTextAsync((await download.PathAsync())!));
        Assert.Equal("1", file.RootElement.GetProperty("digest").GetProperty("contractVersion").GetString());
        Assert.Equal("Shared app", file.RootElement.GetProperty("digest").GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal("rsa-pkcs1-v1_5-sha256", file.RootElement.GetProperty("signature").GetProperty("algorithm").GetString());
        await page.Locator("#shareBackdrop[hidden]").WaitForAsync(new() { State = WaitForSelectorState.Attached });
    }

    [Fact]
    public async Task Connected_consumers_lists_status_and_connects_pauses_and_revokes()
    {
        await using var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.RootUri.ToString(),
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["X-Tenant-Id"] = "t-consumers-browser", ["X-Principal-Id"] = "author", ["X-Principal-Roles"] = "AtlasArchitect"
            }
        });
        var page = await context.NewPageAsync();
        var calls = new List<(string Method, string Path, string? Body)>();
        var connected = false;
        await page.RouteAsync("**/api/v1/share/consumers**", route =>
        {
            var request = route.Request;
            var path = new Uri(request.Url).AbsolutePath;
            calls.Add((request.Method, path, request.PostData));
            if (request.Method == "GET")
            {
                var extra = connected ? """,{"id":"c3","name":"Newly connected","destinationUrl":"https://new.example/api","enrollmentId":"e3","state":"Active","scope":{"kinds":["application"],"tags":[]},"failureCount":0}""" : "";
                return route.FulfillAsync(new()
                {
                    ContentType = "application/json",
                    Body = """[{"id":"c1","name":"Partner","destinationUrl":"https://partner.example/api","enrollmentId":"e1","state":"Active","scope":{"kinds":["system","application"],"tags":[]},"lastSuccessAt":"2026-10-02T10:00:00Z","lastSuccessSequence":7,"failureCount":0},"""
                        + """{"id":"c2","name":"Auditor","destinationUrl":"https://auditor.example/api","enrollmentId":"e2","state":"Stopped","stopReason":"sharing_enrollment_revoked","lastError":"The consumer revoked the sharing. Pushing has stopped.","scope":{"kinds":["vendor"],"tags":[{"key":"shared","value":"true"}]},"failureCount":0}""" + extra + "]"
                });
            }

            if (request.Method == "POST" && path.EndsWith("/api/v1/share/consumers", StringComparison.Ordinal))
            {
                if (request.PostData!.Contains("BADCODE"))
                {
                    return route.FulfillAsync(new() { Status = 400, ContentType = "application/problem+json", Body = """{"title":"Invalid request","status":400,"detail":"The consumer did not accept that code. Check it and try again."}""" });
                }

                connected = true;
                return route.FulfillAsync(new() { Status = 201, ContentType = "application/json", Body = """{"id":"c3"}""" });
            }

            return route.FulfillAsync(new() { ContentType = "application/json", Body = """{"id":"c1"}""" });
        });
        await page.GotoAsync("/");
        await page.Locator("#consumersButton:not([hidden])").WaitForAsync();
        await page.Locator("#consumersButton").ClickAsync();

        // Status is shown per consumer, with the reason when the consumer stopped it.
        var partner = page.GetByLabel("Consumer: Partner");
        await partner.WaitForAsync();
        Assert.Contains("Last accepted push", await partner.InnerTextAsync());
        Assert.Contains("(sequence 7)", await partner.InnerTextAsync());
        var auditor = page.GetByLabel("Consumer: Auditor");
        Assert.Contains("The consumer revoked the sharing", await auditor.InnerTextAsync());
        Assert.Equal(0, await auditor.GetByRole(AriaRole.Button, new() { Name = "Pause" }).CountAsync());   // a stopped consumer cannot be paused or resumed

        // A refused code is explained and nothing is added.
        await page.Locator("#consumerUrl").FillAsync("https://new.example/api");
        await page.Locator("#consumerCode").FillAsync("BADCODE");
        await page.Locator("#consumersSubmit").ClickAsync();
        await page.Locator("#consumersError:not([hidden])").WaitForAsync();
        Assert.Contains("did not accept that code", await page.Locator("#consumersError").InnerTextAsync());

        // A good code connects, with the chosen scope.
        await page.Locator("#consumerCode").FillAsync("AAAA-BBBB");
        await page.Locator("#consumerKinds input[value=system]").UncheckAsync();
        await page.Locator("#consumerKinds input[value=platform]").UncheckAsync();
        await page.Locator("#consumerKinds input[value=vendor]").UncheckAsync();
        await page.Locator("#consumersSubmit").ClickAsync();
        await page.GetByLabel("Consumer: Newly connected").WaitForAsync();
        var post = calls.Last(c => c.Method == "POST" && c.Path.EndsWith("/api/v1/share/consumers", StringComparison.Ordinal));
        using var body = System.Text.Json.JsonDocument.Parse(post.Body!);
        Assert.Equal("AAAA-BBBB", body.RootElement.GetProperty("activationCode").GetString());
        Assert.Equal(["application"], body.RootElement.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()));

        // Pause, change the scope, and revoke (after confirming) go to the consumer's own routes.
        var listings = calls.Count(c => c.Method == "GET");
        await partner.GetByRole(AriaRole.Button, new() { Name = "Pause" }).ClickAsync();
        await Eventually(() => calls.Any(c => c.Path.EndsWith("/c1/pause", StringComparison.Ordinal)));
        await Eventually(() => calls.Count(c => c.Method == "GET") > listings);   // the list is redrawn after the action
        await partner.GetByRole(AriaRole.Button, new() { Name = "Change what is shared" }).ClickAsync();
        await page.Locator("#consumerKinds input[value=application]").UncheckAsync();
        await page.Locator("#consumersSubmit").ClickAsync();
        await Eventually(() => calls.Any(c => c.Method == "PUT"));
        await Eventually(() => calls.Count(c => c.Method == "GET") > listings + 1);
        await partner.GetByRole(AriaRole.Button, new() { Name = "Revoke" }).ClickAsync();
        await page.Locator("#confirmAccept").ClickAsync();
        await Eventually(() => calls.Any(c => c.Path.EndsWith("/c1/revoke", StringComparison.Ordinal)));

        Assert.Contains(calls, c => c.Method == "POST" && c.Path.EndsWith("/api/v1/share/consumers/c1/pause", StringComparison.Ordinal));
        var scope = calls.Single(c => c.Method == "PUT" && c.Path.EndsWith("/api/v1/share/consumers/c1/scope", StringComparison.Ordinal));
        using var scopeBody = System.Text.Json.JsonDocument.Parse(scope.Body!);
        Assert.Equal(["system"], scopeBody.RootElement.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()));
        Assert.Contains(calls, c => c.Method == "POST" && c.Path.EndsWith("/api/v1/share/consumers/c1/revoke", StringComparison.Ordinal));
    }

    private static async Task Eventually(Func<bool> condition, Func<string>? describe = null)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), "The expected request was not made. " + describe?.Invoke());
    }

    [Fact]
    public async Task Share_summary_is_not_offered_to_a_read_only_user()
    {
        await using var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.RootUri.ToString(),
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["X-Tenant-Id"] = "t-share-readonly",
                ["X-Principal-Id"] = "reader",
                ["X-Principal-Roles"] = "AtlasCustomer"
            }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/");
        await page.Locator("#export").WaitForAsync();
        await page.WaitForFunctionAsync("document.querySelector('#newAsset') && document.querySelector('#newAsset').hidden");
        Assert.True(await page.Locator("#shareDigest").IsHiddenAsync());
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
