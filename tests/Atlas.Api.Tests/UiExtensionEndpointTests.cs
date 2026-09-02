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
using Vev.Atlas.Persistence;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// The extension mount surface (atlas#139): <c>GET /api/v1/extensions/ui</c> lists only the ui-extensions
/// the current tenant is entitled to mount. A Community tenant with no grant sees an empty slot; a tenant
/// whose entitlement grants the reserved paid capability is offered the extension with its mount metadata.
/// The decision is server-side and fail-closed — the client only reflects it.
/// </summary>
public sealed class UiExtensionEndpointTests
{
    private static void SetDevHeaders(HttpClient client, string tenant)
    {
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        client.DefaultRequestHeaders.Add("X-Principal-Id", "viewer");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasCustomer");
    }

    [Fact]
    public async Task An_unentitled_community_tenant_is_offered_no_ui_extensions()
    {
        using var factory = new AtlasApiFactory();
        using var client = factory.CreateClient();
        SetDevHeaders(client, "t-ext-denied");

        var response = await client.GetAsync("/api/v1/extensions/ui");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("extensions").EnumerateArray());
    }

    [Fact]
    public async Task An_entitled_tenant_is_offered_the_ui_extension_with_its_mount_metadata()
    {
        using var factory = new EntitledUiExtensionFactory();
        using var client = factory.CreateClient();
        SetDevHeaders(client, "t-ext-granted");

        var response = await client.GetAsync("/api/v1/extensions/ui");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var extension = Assert.Single(body.GetProperty("extensions").EnumerateArray());
        Assert.Equal("com.vev.atlas.portfolio-health", extension.GetProperty("id").GetString());
        Assert.Equal("landscape-right-rail", extension.GetProperty("slot").GetString());
        Assert.Equal("Portfolio health", extension.GetProperty("title").GetString());
        Assert.Equal("https://enterprise.local/portfolio-health", extension.GetProperty("fragmentUrl").GetString());
    }

    /// <summary>
    /// Grants the reserved paid capability and configures a content source, so the entitled path can be
    /// exercised over HTTP through the real endpoint, catalogue, guard and gate.
    /// </summary>
    private sealed class EntitledUiExtensionFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();
            builder.UseEnvironment("Development");
            builder.UseSetting("Atlas:Extensions:PortfolioHealth:FragmentUrl", "https://enterprise.local/portfolio-health");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AtlasDbContext>>();
                services.AddDbContext<AtlasDbContext>(options => options.UseSqlite(_connection));

                var entitlements = new CommunityEntitlementService(
                    new HashSet<string>(StringComparer.Ordinal) { AtlasCapabilities.PortfolioAnalysis.Value });
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
