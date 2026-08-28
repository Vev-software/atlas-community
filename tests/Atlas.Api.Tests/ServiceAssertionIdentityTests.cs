using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vev.Atlas.Contracts;
using Vev.Atlas.Fabric.Dev;
using Vev.Atlas.Persistence;
using Vev.Fabric.Contracts.Identity;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// The <c>service-assertion</c> identity mode: a trusted machine caller authenticates with a short-lived,
/// ECDSA-signed Fabric service-identity assertion that Atlas verifies with only the caller's public key,
/// and acts on behalf of the tenant the assertion names. Fails closed on a missing/invalid/expired/wrong
/// -audience assertion, and refuses to start without the verifying key/issuer/audience configured.
/// </summary>
public sealed class ServiceAssertionIdentityTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string KeyId = "kid-1";
    private const string Issuer = "vev:service/handoff-caller";
    private const string Audience = "vev:service/atlas-community";

    [Fact]
    public async Task A_valid_assertion_writes_on_behalf_of_the_asserted_tenant()
    {
        using var host = new ServiceAssertionTestHost();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ServiceAssertionContextMiddleware.AssertionHeader, Mint("t-assertion"));

        var created = await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("a-svc", AssetKind.Application, "A", Lifecycle.Active), Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // Attributed to the tenant signed into the assertion — proof the assertion authenticated a
        // tenant-scoped caller (not a bare header).
        var audit = host.Services.GetRequiredService<InMemoryAuditSink>();
        Assert.Contains(audit.Events, e => e.Tenant.TenantId == "t-assertion");
    }

    [Fact]
    public async Task A_request_with_no_assertion_is_refused()
    {
        using var host = new ServiceAssertionTestHost();
        var client = host.CreateClient();

        var response = await client.GetAsync("/api/v1/assets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_assertion_signed_by_another_key_is_refused()
    {
        using var host = new ServiceAssertionTestHost();
        var client = host.CreateClient();

        // A well-formed assertion, but signed by a key Atlas does not trust.
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = new ServiceAssertionIssuer(attacker, Issuer, KeyId)
            .Issue(Audience, "svc", "t-assertion", ["catalogue.write"], TimeSpan.FromMinutes(5));
        client.DefaultRequestHeaders.Add(ServiceAssertionContextMiddleware.AssertionHeader, forged);

        var response = await client.GetAsync("/api/v1/assets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_assertion_for_another_audience_is_refused()
    {
        using var host = new ServiceAssertionTestHost();
        var client = host.CreateClient();
        var wrongAudience = NewIssuer().Issue("vev:service/not-us", "svc", "t-assertion", ["catalogue.write"], TimeSpan.FromMinutes(5));
        client.DefaultRequestHeaders.Add(ServiceAssertionContextMiddleware.AssertionHeader, wrongAudience);

        var response = await client.GetAsync("/api/v1/assets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_assertion_is_refused()
    {
        using var host = new ServiceAssertionTestHost();
        var client = host.CreateClient();
        var expired = ServiceAssertionIssuer.FromPem(Issuer, KeyId, Keys.PrivatePem, new FixedClock(DateTimeOffset.UtcNow.AddHours(-1)))
            .Issue(Audience, "svc", "t-assertion", ["catalogue.write"], TimeSpan.FromMinutes(5));
        client.DefaultRequestHeaders.Add(ServiceAssertionContextMiddleware.AssertionHeader, expired);

        var response = await client.GetAsync("/api/v1/assets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void The_host_refuses_to_start_without_the_verifying_key_configured()
    {
        using var host = new ServiceAssertionTestHost(configureKey: false);

        // Identity wiring throws during app build when the mode is service-assertion but no verifying key
        // is configured — fail closed, never accept an unverifiable service call.
        Assert.ThrowsAny<Exception>(() => host.CreateClient());
    }

    private static ServiceAssertionIssuer NewIssuer() => ServiceAssertionIssuer.FromPem(Issuer, KeyId, Keys.PrivatePem);

    private static string Mint(string tenantId) =>
        NewIssuer().Issue(Audience, "svc-handoff", tenantId, ["catalogue.write"], TimeSpan.FromMinutes(5));

    private static readonly (string PrivatePem, string PublicPem) Keys = CreateKeys();

    private static (string, string) CreateKeys()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ServiceAssertionTestHost(bool configureKey = true) : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();

            builder.UseEnvironment("Production");
            builder.UseSetting(RequestIdentityConfiguration.ModeKey, RequestIdentityConfiguration.ServiceAssertion);
            builder.UseSetting(RequestIdentityConfiguration.ServiceAssertionKeyIdKey, KeyId);
            builder.UseSetting(RequestIdentityConfiguration.ServiceAssertionIssuerKey, Issuer);
            builder.UseSetting(RequestIdentityConfiguration.ServiceAssertionAudienceKey, Audience);
            if (configureKey)
            {
                builder.UseSetting(RequestIdentityConfiguration.ServiceAssertionPublicKeyKey, Keys.PublicPem);
            }

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
