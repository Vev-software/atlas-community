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
using Vev.Atlas.Domain;
using Vev.Atlas.Persistence;
using Vev.Fabric.Contracts.Entitlements;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// The whole license chain, end to end (atlas#141): a <b>real signed entitlement snapshot</b> — minted and
/// HMAC-signed exactly the way the self-host license tool does — is handed to the Community host as
/// configuration, and the host's own entitlement evaluator verifies the signature, evaluates the grants, and
/// gates the mount surface. An all-inclusive license unlocks the paid "Portfolio health" ui-extension in the
/// Community UI; a Community snapshot without the grant, or one signed by an untrusted key, leaves it locked.
/// This exercises the signature-verify → evaluate → guard → gate → <c>GET /api/v1/extensions/ui</c> path that
/// the existing catalog/endpoint tests skip by injecting a plain grant set.
/// </summary>
public sealed class EntitlementLicenseEndToEndTests
{
    private const string PortfolioHealthId = "com.vev.atlas.portfolio-health";
    private const string FragmentUrl = "https://enterprise.example/portfolio-health";
    private const string LicensedTenant = "acme";

    [Fact]
    public async Task A_full_license_unlocks_the_paid_portfolio_health_extension_in_the_community_host()
    {
        using var factory = new LicensedHost(SignedLicense.Mint(EntitlementOffer.SelfHostedEnterprise, LicensedTenant));
        using var client = factory.CreateClient();
        SetDevHeaders(client, LicensedTenant);

        var body = await GetExtensionsResponseAsync(client);
        Assert.Equal(UiExtensionContracts.ExtensionsContractVersion, body.GetProperty("contractVersion").GetString());
        var extension = Assert.Single(body.GetProperty("extensions").EnumerateArray());
        Assert.Equal(PortfolioHealthId, extension.GetProperty("id").GetString());
        Assert.Equal("landscape-right-rail", extension.GetProperty("slot").GetString());
        var mount = extension.GetProperty("mount");
        Assert.Equal(UiExtensionContracts.FragmentMountKind, mount.GetProperty("kind").GetString());
        Assert.Equal(UiExtensionContracts.FragmentMountContractVersion, mount.GetProperty("contractVersion").GetString());
        Assert.Equal(FragmentUrl, mount.GetProperty("url").GetString());
    }

    [Fact]
    public async Task A_community_license_without_the_grant_leaves_the_paid_extension_locked()
    {
        using var factory = new LicensedHost(SignedLicense.Mint(EntitlementOffer.CommunitySelfHosted, LicensedTenant));
        using var client = factory.CreateClient();
        SetDevHeaders(client, LicensedTenant);

        var body = await GetExtensionsResponseAsync(client);
        Assert.Equal(UiExtensionContracts.ExtensionsContractVersion, body.GetProperty("contractVersion").GetString());
        Assert.Empty(body.GetProperty("extensions").EnumerateArray());
    }

    [Fact]
    public async Task A_license_signed_by_an_untrusted_key_is_rejected_and_the_extension_stays_locked()
    {
        // Same all-inclusive grants, but the host is configured to trust a DIFFERENT key than the one that
        // signed the document: signature verification fails and the evaluator is fail-closed.
        var tampered = SignedLicense.Mint(EntitlementOffer.SelfHostedEnterprise, LicensedTenant) with
        {
            TrustedKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        };
        using var factory = new LicensedHost(tampered);
        using var client = factory.CreateClient();
        SetDevHeaders(client, LicensedTenant);

        var body = await GetExtensionsResponseAsync(client);
        Assert.Equal(UiExtensionContracts.ExtensionsContractVersion, body.GetProperty("contractVersion").GetString());
        Assert.Empty(body.GetProperty("extensions").EnumerateArray());
    }

    [Fact]
    public async Task A_licensed_host_offers_the_extension_but_ships_no_view_when_no_content_source_is_configured()
    {
        // The open-source client carries no Enterprise view: with no FragmentUrl configured, an entitled
        // tenant is still offered the extension, but its content source is null — the view lives elsewhere.
        using var factory = new LicensedHost(SignedLicense.Mint(EntitlementOffer.SelfHostedEnterprise, LicensedTenant), configureFragmentUrl: false);
        using var client = factory.CreateClient();
        SetDevHeaders(client, LicensedTenant);

        var body = await GetExtensionsResponseAsync(client);
        Assert.Equal(UiExtensionContracts.ExtensionsContractVersion, body.GetProperty("contractVersion").GetString());
        var extension = Assert.Single(body.GetProperty("extensions").EnumerateArray());
        Assert.Equal(PortfolioHealthId, extension.GetProperty("id").GetString());
        var mount = extension.GetProperty("mount");
        Assert.Equal(UiExtensionContracts.FragmentMountKind, mount.GetProperty("kind").GetString());
        Assert.Equal(UiExtensionContracts.FragmentMountContractVersion, mount.GetProperty("contractVersion").GetString());
        // No content source is shipped by the open-source host: the fragment URL is absent (or null).
        var hasContent = mount.TryGetProperty("url", out var fragment) && fragment.ValueKind == JsonValueKind.String;
        Assert.False(hasContent);
    }

    private static async Task<JsonElement> GetExtensionsResponseAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/extensions/ui");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static void SetDevHeaders(HttpClient client, string tenant)
    {
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        client.DefaultRequestHeaders.Add("X-Principal-Id", "viewer");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasCustomer");
    }

    /// <summary>A signed license document plus the trust anchor the host is configured with — mirrors the self-host tool.</summary>
    private sealed record SignedLicense(string DocumentJson, string KeyId, string TrustedKeyBase64)
    {
        private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);

        public static SignedLicense Mint(EntitlementOffer offer, string tenant)
        {
            var key = RandomNumberGenerator.GetBytes(32);
            var keyId = $"e2e-{Guid.NewGuid():N}";
            var issuedAt = DateTimeOffset.UtcNow.AddMinutes(-1);

            var request = new EntitlementBundleRequest(
                tenant, offer, EntitlementLifecycleState.Active,
                issuedAt, issuedAt.AddDays(365), issuedAt.AddDays(395));
            var snapshot = new EntitlementBundleResolver().Resolve(request).Snapshot;

            // HMAC-SHA256 over the camelCase payload, exactly as the reference verifier recomputes it.
            var payload = JsonSerializer.Serialize(snapshot, CamelCase);
            using var hmac = new HMACSHA256(key);
            var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
            var document = new SignedEntitlementSnapshot(keyId, "HS256", payload, signature);

            return new SignedLicense(JsonSerializer.Serialize(document, CamelCase), keyId, Convert.ToBase64String(key));
        }
    }

    /// <summary>The real Community host, configured with a signed snapshot the way a self-hosted install would be.</summary>
    private sealed class LicensedHost(EntitlementLicenseEndToEndTests.SignedLicense license, bool configureFragmentUrl = true)
        : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();
            builder.UseEnvironment("Development");
            if (configureFragmentUrl)
            {
                builder.UseSetting("Atlas:Extensions:PortfolioHealth:FragmentUrl", FragmentUrl);
            }

            // Point the host's entitlement evaluator at the signed snapshot + its trust anchor via config only —
            // no service is replaced, so the real verify/evaluate path runs.
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
