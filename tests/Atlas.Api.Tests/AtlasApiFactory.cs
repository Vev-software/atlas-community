using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vev.Atlas.Persistence;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// Hosts the real Atlas API in-process against an isolated in-memory SQLite database, so the tests
/// exercise the full stack (routing → domain → Fabric shim → persistence) rather than mocks.
/// </summary>
public sealed class AtlasApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open(); // Keep the in-memory database alive for the lifetime of the factory.

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
