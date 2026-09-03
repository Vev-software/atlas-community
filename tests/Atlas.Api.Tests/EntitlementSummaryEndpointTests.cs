using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vev.Atlas.Persistence;
using Vev.Fabric.Contracts.Entitlements;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// The account panel's data source (atlas#147): <c>GET /api/v1/entitlements/summary</c> composes the same
/// entitlement decisions the gates use into one read-only view. A pure Community host reports the Community
/// edition with every paid capability reserved; a host configured with a real signed all-inclusive licence
/// reports the Licensed edition with the paid capabilities enabled. The client only renders this decision.
/// </summary>
public sealed class EntitlementSummaryEndpointTests
{
    private const string PortfolioAnalysisCapability = "atlas.analysis.apm";

    [Fact]
    public async Task Community_host_reports_the_community_edition_with_paid_capabilities_reserved()
    {
        using var factory = new AtlasApiFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-community");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "ada");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");

        var response = await client.GetAsync("/api/v1/entitlements/summary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Community", body.GetProperty("edition").GetString());
        Assert.Equal("community", body.GetProperty("licence").GetProperty("state").GetString());

        var identity = body.GetProperty("identity");
        Assert.Equal("t-community", identity.GetProperty("tenant").GetString());
        Assert.Contains("AtlasArchitect", identity.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

        var capabilities = body.GetProperty("capabilities").EnumerateArray().ToArray();
        Assert.NotEmpty(capabilities);
        Assert.All(capabilities, c => Assert.False(c.GetProperty("enabled").GetBoolean()));
        Assert.All(capabilities, c => Assert.Equal("entitlement_denied", c.GetProperty("reasonCode").GetString()));

        // The free landscape-structuring allowance is still reported — it is Community's own affordance.
        Assert.Equal("atlas.ai.structure", body.GetProperty("aiStructure").GetProperty("capability").GetString());
    }

    [Fact]
    public async Task Licensed_host_reports_the_licensed_edition_with_paid_capabilities_enabled()
    {
        using var factory = new LicensedHost(SignedLicense.Mint(EntitlementOffer.SelfHostedEnterprise, "acme"));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "acme");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "viewer");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasCustomer");

        var response = await client.GetAsync("/api/v1/entitlements/summary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Licensed", body.GetProperty("edition").GetString());
        var state = body.GetProperty("licence").GetProperty("state").GetString();
        Assert.Contains(state, new[] { "active", "expiring" });

        var capabilities = body.GetProperty("capabilities").EnumerateArray().ToArray();
        var portfolio = Assert.Single(capabilities, c => c.GetProperty("capability").GetString() == PortfolioAnalysisCapability);
        Assert.True(portfolio.GetProperty("enabled").GetBoolean());
        // A licence window is surfaced so the panel can show "valid until" / nudge a renewal.
        Assert.False(string.IsNullOrEmpty(body.GetProperty("licence").GetProperty("validUntil").GetString()));
    }

    /// <summary>A signed licence document plus its trust anchor — mirrors the self-host licence tool.</summary>
    private sealed record SignedLicense(string DocumentJson, string KeyId, string TrustedKeyBase64)
    {
        private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);

        public static SignedLicense Mint(EntitlementOffer offer, string tenant)
        {
            var key = RandomNumberGenerator.GetBytes(32);
            var keyId = $"summary-{Guid.NewGuid():N}";
            var issuedAt = DateTimeOffset.UtcNow.AddMinutes(-1);

            var request = new EntitlementBundleRequest(
                tenant, offer, EntitlementLifecycleState.Active,
                issuedAt, issuedAt.AddDays(365), issuedAt.AddDays(395));
            var snapshot = new EntitlementBundleResolver().Resolve(request).Snapshot;

            var payload = JsonSerializer.Serialize(snapshot, CamelCase);
            using var hmac = new HMACSHA256(key);
            var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
            var document = new SignedEntitlementSnapshot(keyId, "HS256", payload, signature);

            return new SignedLicense(JsonSerializer.Serialize(document, CamelCase), keyId, Convert.ToBase64String(key));
        }
    }

    /// <summary>The real Community host, configured with a signed snapshot the way a self-hosted install would be.</summary>
    private sealed class LicensedHost(EntitlementSummaryEndpointTests.SignedLicense license)
        : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();
            builder.UseEnvironment("Development");
            builder.UseSetting("Atlas:Entitlements:SnapshotDocumentJson", license.DocumentJson);
            builder.UseSetting($"Atlas:Entitlements:TrustedKeys:{license.KeyId}", license.TrustedKeyBase64);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AtlasDbContext>>();
                services.AddDbContext<AtlasDbContext>(options => options.UseSqlite(_connection));
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
