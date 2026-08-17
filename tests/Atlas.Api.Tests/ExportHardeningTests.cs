using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Vev.Atlas.Api;
using Vev.Atlas.Fabric.Dev;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// Hardening for the whole-landscape export (atlas#36): a full-map export is the highest-value
/// reconnaissance read, so it must be an authorized, audited, rate-limited action — not a silent bulk
/// read. These tests assert the three guarantees: denied without the elevated role, audited on success,
/// and throttled under repeated calls.
/// </summary>
public sealed class ExportHardeningTests(AtlasApiFactory factory) : IClassFixture<AtlasApiFactory>
{
    private HttpClient Client(string tenant, string roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        client.DefaultRequestHeaders.Add("X-Principal-Id", "p");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", roles);
        return client;
    }

    [Fact]
    public async Task A_read_only_customer_cannot_export_the_whole_landscape()
    {
        var response = await Client(tenant: "t-export-denied", roles: "AtlasCustomer").GetAsync("/api/v1/export");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("role_missing", body);   // machine-readable reason code, never a bare 403
    }

    [Fact]
    public async Task A_successful_export_emits_exactly_one_audit_record_with_scope_and_format()
    {
        var client = Client(tenant: "t-export-audit", roles: "AtlasArchitect");

        var response = await client.GetAsync("/api/v1/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var audit = factory.Services.GetRequiredService<InMemoryAuditSink>();
        var exportEvents = audit.Events
            .Where(e => e.Action == "atlas.landscape.exported" && e.Tenant.TenantId == "t-export-audit")
            .ToList();

        var recorded = Assert.Single(exportEvents);
        Assert.Equal("p", recorded.Actor.PrincipalId);
        Assert.Contains("format=atlas-json", recorded.Resource.Value);   // format captured
        Assert.Contains("scope=full", recorded.Resource.Value);          // scope captured
        Assert.NotEqual(default, recorded.OccurredAt);             // timestamp captured
    }

    [Fact]
    public async Task Repeated_exports_are_throttled()
    {
        // A dedicated host with a tiny per-tenant window, so the third export in the window is rejected.
        using var throttled = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting(ExportRateLimit.PermitLimitKey, "2");
            b.UseSetting(ExportRateLimit.WindowSecondsKey, "60");
        });

        var client = throttled.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-export-throttle");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "p");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");

        var first = await client.GetAsync("/api/v1/export");
        var second = await client.GetAsync("/api/v1/export");
        var third = await client.GetAsync("/api/v1/export");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }
}
