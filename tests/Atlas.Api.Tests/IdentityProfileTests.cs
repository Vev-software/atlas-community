using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vev.Atlas.Contracts;
using Vev.Atlas.Fabric.Dev;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// Fail-closed request identity (atlas#34). The header identity shim is a Development-only convenience;
/// outside Development it is never wired, and with no real identity provider (Fabric OIDC, fabric#3) the
/// host refuses to start rather than trust caller-supplied identity headers.
/// </summary>
public sealed class IdentityProfileTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Development_resolves_identity_from_the_header_shim()
    {
        using var factory = new AtlasApiFactory();   // pinned to the Development environment
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-dev-identity");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");

        var created = await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("a-dev", AssetKind.Application, "A", Lifecycle.Active), Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // The write is attributed to the header-asserted tenant — proof the shim is the identity source in dev.
        var audit = factory.Services.GetRequiredService<InMemoryAuditSink>();
        Assert.Contains(audit.Events, e => e.Tenant.TenantId == "t-dev-identity");
    }

    [Fact]
    public void A_non_development_host_fails_closed_when_no_identity_provider_is_configured()
    {
        using var factory = new AtlasApiFactory()
            .WithWebHostBuilder(b => b.UseEnvironment(Environments.Production));

        // Starting the host runs the identity gate; with no provider it must throw, not fall back to headers.
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Refusing to start", Flatten(ex));
    }

    [Fact]
    public async Task Single_tenant_self_host_binds_a_fixed_identity_and_ignores_request_headers()
    {
        using var factory = new AtlasApiFactory().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(Environments.Production);
            b.UseSetting(RequestIdentityConfiguration.ModeKey, RequestIdentityConfiguration.SingleTenant);
            b.UseSetting(RequestIdentityConfiguration.TenantKey, "community");
            b.UseSetting(RequestIdentityConfiguration.RolesKey, "AtlasArchitect");
        });

        var client = factory.CreateClient();
        // A hostile caller tries to name another tenant and demote itself to a read-only role.
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "attacker-tenant");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasCustomer");

        var created = await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("a-st", AssetKind.Application, "A", Lifecycle.Active), Json);

        // The write succeeds because the configured role (Architect) is used, not the header's Customer,
        // and it is attributed to the configured tenant, not the header's "attacker-tenant".
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var audit = factory.Services.GetRequiredService<InMemoryAuditSink>();
        Assert.Contains(audit.Events, e => e.Tenant.TenantId == "community");
        Assert.DoesNotContain(audit.Events, e => e.Tenant.TenantId == "attacker-tenant");
    }

    [Fact]
    public void The_header_shim_cannot_be_forced_on_outside_development()
    {
        using var factory = new AtlasApiFactory().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(Environments.Staging);
            b.UseSetting(RequestIdentityConfiguration.ModeKey, RequestIdentityConfiguration.DevHeaders);
        });

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("only permitted in the Development environment", Flatten(ex));
    }

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
