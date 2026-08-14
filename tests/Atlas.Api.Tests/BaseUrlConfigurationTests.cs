using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Vev.Atlas.Api;
using Vev.Atlas.Contracts;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// Canonical URL/path configuration (atlas#19): hostnames and paths are deployment configuration, never a
/// baked-in VEV identity. These tests cover the path generation, the configurable API mount, the runtime
/// config the SPA reads, and that no <c>vev.software</c> hostname is assumed.
/// </summary>
public sealed class BaseUrlConfigurationTests(AtlasApiFactory factory) : IClassFixture<AtlasApiFactory>
{
    // --- Path generation (no host in hand) --------------------------------------------------------------

    [Fact]
    public void Defaults_are_a_flat_single_host_shape()
    {
        var urls = new AtlasUrls(new AtlasUrlOptions());
        Assert.Equal("", urls.PathBase);        // root
        Assert.Equal("/api", urls.ApiBasePath);
        Assert.Equal("/login", urls.LoginPath);
    }

    [Theory]
    [InlineData("atlas/", "/atlas")]
    [InlineData("/atlas/", "/atlas")]
    [InlineData("//gateway//", "/gateway")]
    [InlineData("/", "")]
    [InlineData("", "")]
    public void Path_segments_are_normalized(string configured, string expected)
    {
        Assert.Equal(expected, new AtlasUrls(new AtlasUrlOptions { PathBase = configured }).PathBase);
    }

    [Fact]
    public void Absolute_urls_use_the_request_host_when_no_public_base_url_is_configured()
    {
        var urls = new AtlasUrls(new AtlasUrlOptions());
        var request = Request(scheme: "https", host: "atlas.acme.example", pathBase: "/atlas");

        var absolute = urls.AbsoluteUrl(request, "/api/v1/landscape");

        Assert.Equal("https://atlas.acme.example/atlas/api/v1/landscape", absolute);
        Assert.DoesNotContain("vev.software", absolute);   // never a baked-in VEV hostname
    }

    [Fact]
    public void A_configured_public_base_url_wins_over_the_request_host()
    {
        var urls = new AtlasUrls(new AtlasUrlOptions { PublicBaseUrl = "https://atlas.acme.example/" });
        var request = Request(scheme: "http", host: "internal-node:8080", pathBase: "");

        Assert.Equal("https://atlas.acme.example/api/v1/landscape", urls.AbsoluteUrl(request, "/api/v1/landscape"));
    }

    [Fact]
    public void The_client_api_base_includes_the_request_path_base()
    {
        var urls = new AtlasUrls(new AtlasUrlOptions());
        Assert.Equal("/atlas/api", urls.ClientApiBase(Request(pathBase: "/atlas")));
        Assert.Equal("/api", urls.ClientApiBase(Request(pathBase: "")));
    }

    // --- Runtime behaviour over the real host -----------------------------------------------------------

    [Fact]
    public async Task By_default_the_api_is_at_slash_api_and_the_spa_config_reports_it()
    {
        var client = Client();

        // Seed through the canonical route so the default really is /api.
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("app-url", AssetKind.Application, "A", Lifecycle.Active));
        var landscape = await client.GetAsync("/api/v1/landscape");
        Assert.Equal(HttpStatusCode.OK, landscape.StatusCode);

        var appConfig = await client.GetAsync("/app-config.js");
        Assert.Equal(HttpStatusCode.OK, appConfig.StatusCode);
        Assert.Equal("application/javascript", appConfig.Content.Headers.ContentType?.MediaType);
        var js = await appConfig.Content.ReadAsStringAsync();
        Assert.Contains("\"apiBase\":\"/api\"", js);
        Assert.DoesNotContain("vev.software", js);
    }

    [Fact]
    public async Task The_api_mount_path_is_configurable()
    {
        using var custom = factory.WithWebHostBuilder(b => b.UseSetting("Atlas:Urls:ApiBasePath", "/gateway"));
        var client = custom.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-url");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");

        // The API now answers under the configured mount, and the old default 404s.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/gateway/v1/landscape")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/landscape")).StatusCode);

        var js = await (await client.GetAsync("/app-config.js")).Content.ReadAsStringAsync();
        Assert.Contains("\"apiBase\":\"/gateway\"", js);
    }

    [Fact]
    public async Task Under_a_reverse_proxy_sub_path_everything_hangs_off_the_path_base()
    {
        using var subPath = factory.WithWebHostBuilder(b => b.UseSetting("Atlas:Urls:PathBase", "/atlas"));
        var client = subPath.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-url");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");

        // UI, health and API all answer under the base path (UsePathBase strips it before routing), which
        // is what a reverse proxy forwards. It does not forbid direct un-prefixed access — that is expected.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/atlas/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/atlas/api/v1/landscape")).StatusCode);

        // The SPA config reports the path-base-included API base, so the browser calls the right place.
        var js = await (await client.GetAsync("/atlas/app-config.js")).Content.ReadAsStringAsync();
        Assert.Contains("\"apiBase\":\"/atlas/api\"", js);
    }

    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-url");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "arch");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");
        return client;
    }

    private static HttpRequest Request(string scheme = "https", string host = "localhost", string pathBase = "")
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = scheme;
        http.Request.Host = new HostString(host);
        http.Request.PathBase = pathBase;
        return http.Request;
    }
}
