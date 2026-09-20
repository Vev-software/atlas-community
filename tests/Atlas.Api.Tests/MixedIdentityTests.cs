using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Vev.Fabric.Contracts.Identity;
using Xunit;

namespace Vev.Atlas.Api.Tests;

public sealed class MixedIdentityTests
{
    [Fact]
    public async Task Users_and_signed_machines_share_a_catalogue_without_header_fallback()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issuer = ServiceAssertionIssuer.FromPem("test-service", "test", key.ExportPkcs8PrivateKeyPem());
        using var oidc = new OidcTestHost();
        using var host = oidc.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Atlas:Identity:AllowedTenant", "t-oidc");
            builder.UseSetting("Atlas:Identity:ServiceAssertion:Enabled", "true");
            builder.UseSetting("Atlas:Identity:ServiceAssertion:PublicKeyPem", key.ExportSubjectPublicKeyInfoPem());
            builder.UseSetting("Atlas:Identity:ServiceAssertion:KeyId", "test");
            builder.UseSetting("Atlas:Identity:ServiceAssertion:Issuer", "test-service");
            builder.UseSetting("Atlas:Identity:ServiceAssertion:Audience", "catalogue");
        });
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/assets")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oidc.CreateToken());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/assets")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Fabric-Service-Assertion", "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/assets")).StatusCode);
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("X-Fabric-Service-Assertion", issuer.Issue("catalogue", "reader", "t-oidc", [], TimeSpan.FromMinutes(5)));
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "forged");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/assets")).StatusCode);
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("X-Fabric-Service-Assertion", issuer.Issue("catalogue", "reader", "other", [], TimeSpan.FromMinutes(5)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/assets")).StatusCode);
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oidc.CreateToken(tenant: "other"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/assets")).StatusCode);
    }
}
