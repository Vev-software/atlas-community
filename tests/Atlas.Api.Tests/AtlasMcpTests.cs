using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Vev.Atlas.Contracts;
using Vev.Atlas.Fabric.Dev;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// MCP integration tests: the tenant catalogue is exposed as a governed, read-only tool surface for
/// the customer's own AI agent.
/// </summary>
public sealed class AtlasMcpTests(AtlasApiFactory factory) : IClassFixture<AtlasApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Client(string tenant = "acme", string principal = "viewer", string roles = "AtlasCustomer")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        client.DefaultRequestHeaders.Add("X-Principal-Id", principal);
        client.DefaultRequestHeaders.Add("X-Principal-Roles", roles);
        client.DefaultRequestHeaders.Add("Accept", "application/json, text/event-stream");
        return client;
    }

    [Fact]
    public async Task Mcp_lists_only_the_governed_read_tools()
    {
        using var client = Client();

        var init = await SendMcpAsync(client, "initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "atlas-tests", version = "1.0" }
        });

        Assert.Equal("2025-06-18", init.GetProperty("result").GetProperty("protocolVersion").GetString());

        var listed = await SendMcpAsync(client, "tools/list", new { });
        var names = listed.GetProperty("result").GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["atlas_export_context_pack", "atlas_get_asset", "atlas_list_assets", "atlas_traverse_relationships"],
            names);
    }

    [Fact]
    public async Task Mcp_list_and_get_asset_are_tenant_scoped_and_audited()
    {
        await AuthorClient("mcp-tenant-a").PostAsJsonAsync(
            "/api/v1/assets",
            new Asset("app-a", AssetKind.Application, "App A", Lifecycle.Active),
            Json);
        await AuthorClient("mcp-tenant-b").PostAsJsonAsync(
            "/api/v1/assets",
            new Asset("app-b", AssetKind.Application, "App B", Lifecycle.Active),
            Json);

        using var client = Client(tenant: "mcp-tenant-a");
        await SendMcpAsync(client, "initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "atlas-tests", version = "1.0" }
        });

        var listed = await SendMcpAsync(client, "tools/call", new
        {
            name = "atlas_list_assets",
            arguments = new { query = "App" }
        });
        var assets = listed.GetProperty("result").GetProperty("structuredContent").GetProperty("result").EnumerateArray().ToArray();
        Assert.Single(assets);
        Assert.Equal("app-a", assets[0].GetProperty("id").GetString());

        var fetched = await SendMcpAsync(client, "tools/call", new
        {
            name = "atlas_get_asset",
            arguments = new { id = "app-a" }
        });
        var asset = fetched.GetProperty("result").GetProperty("structuredContent").GetProperty("asset");
        Assert.Equal("app-a", asset.GetProperty("id").GetString());

        var audit = factory.Services.GetRequiredService<InMemoryAuditSink>();
        Assert.Contains(audit.Events, e => e.TenantId == "mcp-tenant-a" && e.Action == "atlas.mcp.assets.list");
        Assert.Contains(audit.Events, e => e.TenantId == "mcp-tenant-a" && e.Action == "atlas.mcp.asset.get");
        Assert.DoesNotContain(audit.Events, e => e.TenantId == "mcp-tenant-a" && e.Resource.Contains("app-b", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mcp_traversal_and_context_pack_follow_the_catalogue_scope()
    {
        var author = AuthorClient("mcp-tenant-pack");
        await author.PostAsJsonAsync("/api/v1/assets",
            new Asset("app", AssetKind.Application, "App", Lifecycle.Active), Json);
        await author.PostAsJsonAsync("/api/v1/assets",
            new Asset("srv", AssetKind.Server, "Srv", Lifecycle.Active), Json);
        await author.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r1", "app", "srv", RelationshipType.RunsOn), Json);

        using var client = Client(tenant: "mcp-tenant-pack");
        await SendMcpAsync(client, "initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "atlas-tests", version = "1.0" }
        });

        var traversed = await SendMcpAsync(client, "tools/call", new
        {
            name = "atlas_traverse_relationships",
            arguments = new { assetIds = new[] { "app" }, depth = 1 }
        });
        var traversal = traversed.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal(2, traversal.GetProperty("assets").GetArrayLength());
        Assert.Single(traversal.GetProperty("relationships").EnumerateArray());

        var exported = await SendMcpAsync(client, "tools/call", new
        {
            name = "atlas_export_context_pack",
            arguments = new { assetIds = new[] { "app" } }
        });
        var pack = exported.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal("deterministic", pack.GetProperty("mode").GetString());
        Assert.Contains("App", pack.GetProperty("markdown").GetString()!);

        var audit = factory.Services.GetRequiredService<InMemoryAuditSink>();
        Assert.Contains(audit.Events, e => e.TenantId == "mcp-tenant-pack" && e.Action == "atlas.mcp.relationships.traverse");
        Assert.Contains(audit.Events, e => e.TenantId == "mcp-tenant-pack" && e.Action == "atlas.mcp.context-pack.exported");
    }

    private HttpClient AuthorClient(string tenant) => Client(tenant, "arch", "AtlasArchitect");

    private static async Task<JsonElement> SendMcpAsync(HttpClient client, string method, object parameters)
    {
        using var response = await client.PostAsJsonAsync(
            "/mcp",
            new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = parameters },
            Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var data = ExtractDataPayload(body);
        using var document = JsonDocument.Parse(data);
        return document.RootElement.Clone();
    }

    private static string ExtractDataPayload(string sseBody)
    {
        foreach (var line in sseBody.Split('\n'))
        {
            const string prefix = "data: ";
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..].Trim();
            }
        }

        throw new Xunit.Sdk.XunitException($"MCP response did not contain a data frame: {sseBody}");
    }
}
