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
/// The free "paste to landscape" hook (atlas.ai.structure) is Community's own adoption affordance, not a
/// licensed capability. A signed licence that is simply <i>silent</i> on it must not silently revoke it —
/// but a broken / unavailable licence still fails closed. Regression guard for the case where configuring
/// any entitlement snapshot turned the free hook off ("not enabled for the current tenant").
/// </summary>
public sealed class AiStructureFreeHookTests
{
    private const string Tenant = "acme";
    private const string KeyId = "ai-hook-test";

    [Fact]
    public async Task A_valid_licence_silent_on_the_free_hook_keeps_it_available()
    {
        // A real signed licence granting only catalogue.read — it says nothing about atlas.ai.structure.
        using var host = SignedHost.Granting("atlas.catalogue.read");
        using var client = ClientFor(host, Tenant);

        var body = await (await client.GetAsync("/api/v1/ai/allowances")).Content.ReadFromJsonAsync<JsonElement>();
        var hook = Assert.Single(body.GetProperty("capabilities").EnumerateArray());

        Assert.Equal("atlas.ai.structure", hook.GetProperty("capability").GetString());
        Assert.Equal("limited", hook.GetProperty("status").GetString());
        Assert.True(hook.GetProperty("limit").GetInt32() > 0);
    }

    [Fact]
    public async Task A_broken_licence_fails_closed_and_the_hook_stays_unavailable()
    {
        // A snapshot source is configured but the document is missing → the entitlement state is
        // unavailable, and the free hook must NOT be resurrected from a broken licence.
        using var host = SignedHost.WithMissingSnapshot();
        using var client = ClientFor(host, Tenant);

        var body = await (await client.GetAsync("/api/v1/ai/allowances")).Content.ReadFromJsonAsync<JsonElement>();
        var hook = Assert.Single(body.GetProperty("capabilities").EnumerateArray());

        Assert.Equal("unavailable", hook.GetProperty("status").GetString());
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
        private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly SqliteConnection _connection = new("DataSource=:memory:");
        private readonly string _snapshotPath;
        private readonly string _keyBase64;

        private SignedHost(string snapshotPath, string keyBase64)
        {
            _snapshotPath = snapshotPath;
            _keyBase64 = keyBase64;
        }

        public static SignedHost Granting(params string[] capabilities)
        {
            var keyBytes = RandomNumberGenerator.GetBytes(32);
            var now = DateTimeOffset.UtcNow;
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

            var path = Path.Combine(Path.GetTempPath(), $"atlas-aihook-{Guid.NewGuid():N}.snapshot.json");
            File.WriteAllText(path, JsonSerializer.Serialize(signed, CamelCase));
            return new SignedHost(path, Convert.ToBase64String(keyBytes));
        }

        public static SignedHost WithMissingSnapshot()
        {
            // A configured path that does not exist → a present-but-unavailable licence source.
            var path = Path.Combine(Path.GetTempPath(), $"atlas-aihook-missing-{Guid.NewGuid():N}.snapshot.json");
            return new SignedHost(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
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
