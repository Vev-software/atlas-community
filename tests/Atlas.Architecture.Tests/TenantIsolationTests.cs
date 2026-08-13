using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vev.Atlas.Fabric;
using Vev.Atlas.Persistence;
using Xunit;

namespace Vev.Atlas.Architecture.Tests;

/// <summary>
/// Machine-enforced tenant isolation (atlas#35). The catalogue is reconnaissance-grade landscape data,
/// so isolation must hold <b>by default</b> — a query that forgets an explicit <c>TenantId</c> predicate
/// must still be scoped to the caller's tenant, and the build must fail if that guarantee is removed.
/// These tests exercise the EF Core global query filter directly, so deleting or disabling it breaks the
/// build rather than silently opening a cross-tenant read.
/// </summary>
public sealed class TenantIsolationTests
{
    /// <summary>The tenant-scoped tables the global query filter must cover.</summary>
    private static readonly string[] TenantScopedTables = ["assets", "relationships"];

    [Fact]
    public void A_query_that_omits_the_tenant_predicate_is_still_scoped_by_the_global_filter()
    {
        using var connection = OpenSharedInMemory();
        var tenant = new MutableRequestContext();

        // Seed rows for two tenants. Inserts are not filtered, so one context can plant both.
        using (var seed = NewContext(connection, tenant))
        {
            seed.Database.EnsureCreated();
            tenant.TenantId = "tenant-a";
            seed.Assets.Add(Asset("tenant-a", "a-1"));
            seed.Assets.Add(Asset("tenant-b", "b-1"));   // another tenant's row, planted directly
            seed.Relationships.Add(Rel("tenant-a", "ra-1"));
            seed.Relationships.Add(Rel("tenant-b", "rb-1"));
            seed.SaveChanges();
        }

        using var ctx = NewContext(connection, tenant);
        tenant.TenantId = "tenant-a";

        // The queries below carry NO explicit TenantId predicate — they rely solely on the global filter.
        var assets = ctx.Assets.ToList();
        var relationships = ctx.Relationships.ToList();

        Assert.Equal(["a-1"], assets.Select(a => a.Id));
        Assert.Equal(["ra-1"], relationships.Select(r => r.Id));

        // The other tenant's rows really are in the store; only the filter hides them. If the global
        // filter were removed, the assertions above would see both tenants and fail — that is the guard.
        Assert.Equal(2, ctx.Assets.IgnoreQueryFilters().Count());
        Assert.Equal(2, ctx.Relationships.IgnoreQueryFilters().Count());
    }

    [Fact]
    public void Switching_the_ambient_tenant_switches_what_the_same_context_can_read()
    {
        using var connection = OpenSharedInMemory();
        var tenant = new MutableRequestContext { TenantId = "tenant-a" };

        using var ctx = NewContext(connection, tenant);
        ctx.Database.EnsureCreated();
        ctx.Assets.Add(Asset("tenant-a", "a-1"));
        ctx.Assets.Add(Asset("tenant-b", "b-1"));
        ctx.SaveChanges();

        tenant.TenantId = "tenant-b";
        Assert.Equal(["b-1"], ctx.Assets.AsNoTracking().ToList().Select(a => a.Id));

        tenant.TenantId = "tenant-a";
        Assert.Equal(["a-1"], ctx.Assets.AsNoTracking().ToList().Select(a => a.Id));
    }

    [Fact]
    public void Every_tenant_scoped_entity_declares_a_global_query_filter()
    {
        using var connection = OpenSharedInMemory();
        using var ctx = NewContext(connection, new MutableRequestContext { TenantId = "tenant-a" });

        foreach (var table in TenantScopedTables)
        {
            var entity = ctx.Model.GetEntityTypes().SingleOrDefault(e => e.GetTableName() == table);
            Assert.True(entity is not null, $"Expected a tenant-scoped entity mapped to table '{table}'.");
            Assert.True(entity!.GetDeclaredQueryFilters().Any(),
                $"Table '{table}' holds tenant-scoped data but has no global query filter — tenant isolation " +
                "must not depend on every query remembering the predicate (atlas#35).");
        }
    }

    private static SqliteConnection OpenSharedInMemory()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();   // keep the in-memory database alive for the life of the connection
        return connection;
    }

    private static AtlasDbContext NewContext(SqliteConnection connection, IRequestContextAccessor requestContext)
    {
        var options = new DbContextOptionsBuilder<AtlasDbContext>().UseSqlite(connection).Options;
        return new AtlasDbContext(options, requestContext);
    }

    private static AssetRow Asset(string tenantId, string id) => new()
    {
        TenantId = tenantId,
        Id = id,
        Kind = "application",
        Name = id,
        Lifecycle = "active",
        DocumentJson = "{}"
    };

    private static RelationshipRow Rel(string tenantId, string id) => new()
    {
        TenantId = tenantId,
        Id = id,
        FromId = "x",
        ToId = "y",
        Type = "runs-on"
    };

    /// <summary>A request-context accessor whose bound tenant can be moved between queries in a test.</summary>
    private sealed class MutableRequestContext : IRequestContextAccessor
    {
        public string TenantId { get; set; } = "unset";
        public TenantContext Tenant => new(TenantId);
        public PrincipalContext Principal => new("test", "Test", ["AtlasArchitect"]);
    }
}
