using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vev.Atlas.Contracts;
using Vev.Atlas.Domain;
using Vev.Atlas.Fabric.Dev;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// The <c>fabric-oidc</c> identity mode (fabric#3): request identity comes from a verified OIDC bearer
/// token, its claims map to the tenant + principal, and anything without a trustworthy, tenant-bound token
/// is refused. Real multi-tenant identity that replaces the dev header shim and the single-tenant self-host.
/// </summary>
public sealed class OidcIdentityTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_verified_token_binds_identity_from_its_claims()
    {
        using var host = new OidcTestHost();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = Bearer(
            host.CreateToken(tenant: "t-oidc", roles: AtlasRoles.Architect));

        var created = await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("a-oidc", AssetKind.Application, "A", Lifecycle.Active), Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // The write is attributed to the token's tenant — proof the verified token is the identity source.
        var audit = host.Services.GetRequiredService<InMemoryAuditSink>();
        Assert.Contains(audit.Events, e => e.Tenant.TenantId == "t-oidc");
    }

    [Fact]
    public async Task A_request_with_no_token_is_refused()
    {
        using var host = new OidcTestHost();
        var client = host.CreateClient();

        var response = await client.GetAsync("/api/v1/assets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_from_an_untrusted_issuer_is_refused()
    {
        using var host = new OidcTestHost();
        var client = host.CreateClient();
        // Correctly shaped, but signed with a key the host does not trust.
        client.DefaultRequestHeaders.Authorization = Bearer(
            host.CreateToken(host.ForeignKey, tenant: "t-oidc", sub: "u", name: "U", roles: AtlasRoles.Architect));

        var response = await client.GetAsync("/api/v1/assets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_verified_token_without_a_tenant_claim_is_refused()
    {
        using var host = new OidcTestHost();
        var client = host.CreateClient();
        // Validly signed, but carries no tenant — Atlas cannot scope it, so it must not run unscoped.
        client.DefaultRequestHeaders.Authorization = Bearer(
            host.CreateToken(tenant: null, roles: AtlasRoles.Architect));

        var response = await client.GetAsync("/api/v1/assets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Header_asserted_identity_has_no_effect_in_fabric_oidc_mode()
    {
        using var host = new OidcTestHost();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = Bearer(
            host.CreateToken(tenant: "t-token", roles: AtlasRoles.Architect));
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "attacker-tenant");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "attacker-user");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasCustomer");

        var created = await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("a-oidc-headers", AssetKind.Application, "A", Lifecycle.Active), Json);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var audit = host.Services.GetRequiredService<InMemoryAuditSink>();
        Assert.Contains(audit.Events, e =>
            e.Action == "atlas.asset.created" &&
            e.Tenant.TenantId == "t-token" &&
            e.Actor.PrincipalId == "u-oidc");
        Assert.DoesNotContain(audit.Events, e => e.Tenant.TenantId == "attacker-tenant");
        Assert.DoesNotContain(audit.Events, e => e.Actor.PrincipalId == "attacker-user");
    }

    [Fact]
    public async Task Health_is_reachable_without_a_token()
    {
        using var host = new OidcTestHost();
        var client = host.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Fabric_oidc_fails_closed_when_no_provider_is_configured()
    {
        using var factory = new AtlasApiFactory().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(Environments.Production);
            b.UseSetting(RequestIdentityConfiguration.ModeKey, RequestIdentityConfiguration.FabricOidc);
            // No Atlas:Identity:Oidc:Authority — the host must refuse to start rather than trust headers.
        });

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Refusing to start", Flatten(ex));
    }

    private static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);

    /// <summary>Concatenate the message chain so the assertion sees the root cause even if the host wraps it.</summary>
    private static string Flatten(Exception ex)
    {
        var messages = new List<string>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }
}
