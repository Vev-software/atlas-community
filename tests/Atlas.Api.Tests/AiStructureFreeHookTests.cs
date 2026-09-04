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
/// The free landscape-structuring hook (atlas.ai.structure — "Paste to landscape") is an entitlement, not
/// a hardcoded fallback (atlas#149). The allowance is read from the tenant's entitlement: the free tier
/// grants it capped to a daily limit, paid tiers grant it uncapped, a licence that does not grant it leaves
/// it unavailable, and a standalone self-host with no entitlement source at all keeps the free daily hook
/// (handbook §1.9). A broken/missing licence fails closed.
/// </summary>
public sealed class AiStructureFreeHookTests
{
    private const string Tenant = "acme";
    private const string KeyId = "ai-hook-test";

    [Fact]
    public async Task The_community_offer_reports_the_free_hook_capped_to_a_daily_limit()
    {
        using var host = SignedHost.ForOffer(EntitlementOffer.CommunitySelfHosted);
        using var client = ClientFor(host, Tenant);

        var hook = await ReadHook(client);

        Assert.Equal("atlas.ai.structure", hook.GetProperty("capability").GetString());
        Assert.Equal("limited", hook.GetProperty("status").GetString());
        Assert.Equal(3, hook.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task A_paid_offer_reports_the_free_hook_as_unlimited()
    {
        using var host = SignedHost.ForOffer(EntitlementOffer.SelfHostedEnterprise);
        using var client = ClientFor(host, Tenant);

        var hook = await ReadHook(client);

        Assert.Equal("unlimited", hook.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_licence_that_does_not_grant_the_hook_leaves_it_unavailable()
    {
        // A valid, correctly signed licence that grants only the catalogue surface — it does not grant the
        // hook, so under the entitlement-driven model the tenant is not entitled to it.
        using var host = SignedHost.Granting("atlas.catalogue.read");
        using var client = ClientFor(host, Tenant);

        var hook = await ReadHook(client);

        Assert.Equal("unavailable", hook.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_standalone_self_host_without_a_licence_keeps_the_free_daily_hook()
    {
        // No entitlement source at all (offline self-host): the free hook is Community's own affordance.
        using var host = new AtlasApiFactory();
        using var client = ClientFor(host, Tenant);

        var hook = await ReadHook(client);

        Assert.Equal("limited", hook.GetProperty("status").GetString());
        Assert.Equal(3, hook.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task A_broken_licence_fails_closed_and_the_hook_stays_unavailable()
    {
        using var host = SignedHost.WithMissingSnapshot();
        using var client = ClientFor(host, Tenant);

        var hook = await ReadHook(client);

        Assert.Equal("unavailable", hook.GetProperty("status").GetString());
    }

    private static async Task<JsonElement> ReadHook(HttpClient client)
    {
        var body = await (await client.GetAsync("/api/v1/ai/allowances")).Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("capabilities").EnumerateArray().Single();
    }

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string tenant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        client.DefaultRequestHeaders.Add("X-Principal-Id", "arch");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");
        return client;
    }

    private sealed class SignedHost : WebApplicationFactory<Program>
    {
        private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);

        private readonly SqliteConnection _connection = new("DataSource=:memory:");
        private readonly string? _documentJson;
        private readonly string? _snapshotPath;
        private readonly string _keyBase64;

        private SignedHost(string? documentJson, string? snapshotPath, string keyBase64)
        {
            _documentJson = documentJson;
            _snapshotPath = snapshotPath;
            _keyBase64 = keyBase64;
        }

        /// <summary>A licence minted from a real offer bundle (exercises the shipped grants + limits).</summary>
        public static SignedHost ForOffer(EntitlementOffer offer)
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = new EntitlementBundleResolver().Resolve(new EntitlementBundleRequest(
                Tenant, offer, EntitlementLifecycleState.Active, now.AddMinutes(-1), now.AddYears(100), now.AddYears(100))).Snapshot;
            return FromSnapshot(snapshot);
        }

        /// <summary>A hand-built licence granting exactly the given capabilities (no windowed limits).</summary>
        public static SignedHost Granting(params string[] capabilities)
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = new EntitlementSnapshot(
                Tenant,
                now.AddMinutes(-1),
                now.AddYears(100),
                now.AddYears(100),
                capabilities.Select(capability => new EntitlementGrant(capability, "bundle")).ToArray());
            return FromSnapshot(snapshot);
        }

        public static SignedHost WithMissingSnapshot()
        {
            var path = Path.Combine(Path.GetTempPath(), $"atlas-aihook-missing-{Guid.NewGuid():N}.snapshot.json");
            return new SignedHost(documentJson: null, snapshotPath: path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        }

        private static SignedHost FromSnapshot(EntitlementSnapshot snapshot)
        {
            var keyBytes = RandomNumberGenerator.GetBytes(32);
            var payloadJson = JsonSerializer.Serialize(snapshot, CamelCase);
            using var hmac = new HMACSHA256(keyBytes);
            var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadJson)));
            var document = new SignedEntitlementSnapshot(KeyId, "HS256", payloadJson, signature);
            return new SignedHost(JsonSerializer.Serialize(document, CamelCase), snapshotPath: null, Convert.ToBase64String(keyBytes));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();
            builder.UseEnvironment("Development");
            if (_documentJson is not null)
            {
                builder.UseSetting("Atlas:Entitlements:SnapshotDocumentJson", _documentJson);
            }
            else if (_snapshotPath is not null)
            {
                builder.UseSetting("Atlas:Entitlements:SnapshotDocumentPath", _snapshotPath);
            }

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
            }
        }
    }
}
