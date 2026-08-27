using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vev.Atlas.Contracts;
using Vev.Atlas.Domain;
using Vev.Atlas.Fabric.Dev;
using Vev.Atlas.Persistence;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// The <c>service-token</c> identity mode: a trusted machine caller authenticates with a configured
/// shared secret and acts on behalf of the tenant it names in <c>X-Tenant-Id</c>. Fails closed on a
/// missing/wrong token or a missing tenant, and refuses to start without a secret configured.
/// </summary>
public sealed class ServiceTokenIdentityTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_valid_token_writes_on_behalf_of_the_named_tenant()
    {
        using var host = new ServiceTokenTestHost();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ServiceTokenContextMiddleware.TokenHeader, ServiceTokenTestHost.Secret);
        client.DefaultRequestHeaders.Add(ServiceTokenContextMiddleware.TenantHeader, "t-service");

        var created = await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("a-svc", AssetKind.Application, "A", Lifecycle.Active), Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // Attributed to the header-named tenant — proof the token authenticated a tenant-scoped caller.
        var audit = host.Services.GetRequiredService<InMemoryAuditSink>();
        Assert.Contains(audit.Events, e => e.Tenant.TenantId == "t-service");
    }

    [Fact]
    public async Task A_request_with_no_token_is_refused()
    {
        using var host = new ServiceTokenTestHost();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ServiceTokenContextMiddleware.TenantHeader, "t-service");

        var response = await client.GetAsync("/api/v1/assets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_request_with_the_wrong_token_is_refused()
    {
        using var host = new ServiceTokenTestHost();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ServiceTokenContextMiddleware.TokenHeader, "not-the-secret");
        client.DefaultRequestHeaders.Add(ServiceTokenContextMiddleware.TenantHeader, "t-service");

        var response = await client.GetAsync("/api/v1/assets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_token_without_a_tenant_is_refused()
    {
        using var host = new ServiceTokenTestHost();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ServiceTokenContextMiddleware.TokenHeader, ServiceTokenTestHost.Secret);

        var response = await client.GetAsync("/api/v1/assets");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void The_host_refuses_to_start_without_a_configured_secret()
    {
        using var host = new ServiceTokenTestHost(configureSecret: false);

        // The identity wiring throws during app build (first client/server materialisation) when the
        // mode is service-token but no secret is configured — fail closed, never accept unauthenticated.
        Assert.ThrowsAny<Exception>(() => host.CreateClient());
    }

    private sealed class ServiceTokenTestHost(bool configureSecret = true) : WebApplicationFactory<Program>
    {
        public const string Secret = "test-service-secret-value";

        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();

            // A non-Development environment with the mode explicitly selected (atlas#34).
            builder.UseEnvironment("Production");
            builder.UseSetting(RequestIdentityConfiguration.ModeKey, RequestIdentityConfiguration.ServiceToken);
            if (configureSecret)
            {
                builder.UseSetting(RequestIdentityConfiguration.ServiceTokenSecretKey, Secret);
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
