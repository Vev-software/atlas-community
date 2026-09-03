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
using Vev.Fabric.Contracts.Entitlements;
using Vev.Atlas.Persistence;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// The slice that matters to a self-hosting operator: run Community, load a genuinely <b>signed</b>
/// entitlement snapshot, and a reserved paid ui-extension becomes mountable — but only because the
/// snapshot grants its capability. This exercises the real signature-verified consumption path
/// (HMAC verify → local evaluator → gate → extension catalogue), not a test-double entitlement
/// service, so it is the durable guard behind the local end-to-end demo. A validly signed snapshot
/// that does not grant the capability leaves the slot empty — proving it is the grant that unlocks
/// the feature, not merely holding a licence.
/// </summary>
public sealed class SignedEntitlementUnlockTests
{
    private const string Tenant = "acme";
    private const string KeyId = "slice-test";
    private const string PaidCapability = "atlas.analysis.apm";
    private const string PortfolioHealthExtensionId = "com.vev.atlas.portfolio-health";

    [Fact]
    public async Task A_signed_snapshot_that_grants_the_capability_offers_the_ui_extension()
    {
        using var factory = SignedLicense.Granting(PaidCapability);
        using var client = ClientFor(factory, Tenant);

        var body = await (await client.GetAsync("/api/v1/extensions/ui")).Content.ReadFromJsonAsync<JsonElement>();

        var extension = Assert.Single(body.GetProperty("extensions").EnumerateArray());
        Assert.Equal(PortfolioHealthExtensionId, extension.GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_signed_snapshot_without_the_grant_leaves_the_slot_empty()
    {
        // A real, correctly signed licence — but it does not grant the paid capability.
        using var factory = SignedLicense.Granting("atlas.catalogue.read");
        using var client = ClientFor(factory, Tenant);

        var body = await (await client.GetAsync("/api/v1/extensions/ui")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Empty(body.GetProperty("extensions").EnumerateArray());
    }

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string tenant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        client.DefaultRequestHeaders.Add("X-Principal-Id", "arch");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");
        return client;
    }

    /// <summary>Boots Community consuming a snapshot signed the same way the licence tooling signs one.</summary>
    private sealed class SignedLicense : WebApplicationFactory<Program>
    {
        private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly SqliteConnection _connection = new("DataSource=:memory:");
        private readonly string _snapshotPath;
        private readonly string _keyBase64;

        private SignedLicense(string snapshotPath, string keyBase64)
        {
            _snapshotPath = snapshotPath;
            _keyBase64 = keyBase64;
        }

        public static SignedLicense Granting(params string[] capabilities)
        {
            var keyBytes = RandomNumberGenerator.GetBytes(32);
            var now = DateTimeOffset.UtcNow;

            // IssuedAt in the past (so the evaluator's trusted-time floor never overshoots the clock);
            // a far-future expiry + grace so the test never becomes a time bomb. Source "bundle" (not
            // "trial") so the purchased fail-static path applies, mirroring a real licence.
            var payload = new EntitlementSnapshot(
                Tenant,
                now.AddDays(-1),
                now.AddYears(100),
                now.AddYears(100),
                capabilities.Select(capability => new EntitlementGrant(capability, "bundle")).ToArray());

            var payloadJson = JsonSerializer.Serialize(payload, CamelCase);
            using var hmac = new HMACSHA256(keyBytes);
            var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadJson)));
            var signed = new SignedEntitlementSnapshot(KeyId, "HS256", payloadJson, signature);

            var path = Path.Combine(Path.GetTempPath(), $"atlas-slice-{Guid.NewGuid():N}.snapshot.json");
            File.WriteAllText(path, JsonSerializer.Serialize(signed, CamelCase));

            return new SignedLicense(path, Convert.ToBase64String(keyBytes));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();
            builder.UseEnvironment("Development");
            builder.UseSetting("Atlas:Entitlements:SnapshotDocumentPath", _snapshotPath);
            builder.UseSetting($"Atlas:Entitlements:TrustedKeys:{KeyId}", _keyBase64);
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
                try { File.Delete(_snapshotPath); } catch (IOException) { }
            }
        }
    }
}
