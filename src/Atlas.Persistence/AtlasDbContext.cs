using Microsoft.EntityFrameworkCore;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Persistence;

/// <summary>
/// EF Core context for the Community catalogue. Kept deliberately small.
/// <para>
/// Tenant isolation is <b>defense in depth</b>: every tenant-scoped entity carries a global query
/// filter keyed on the ambient request tenant (<see cref="IRequestContextAccessor"/>), so a query that
/// forgets an explicit <c>TenantId</c> predicate is still scoped to the caller's tenant by default. The
/// filter never fails open — the only way past it is the explicit, greppable EF opt-out
/// <c>IgnoreQueryFilters()</c>, which the architecture fitness tests require to be audited. Isolation is
/// therefore enforced by the model, not by every developer remembering the predicate (atlas#35).
/// </para>
/// </summary>
public sealed class AtlasDbContext(DbContextOptions<AtlasDbContext> options, IRequestContextAccessor requestContext)
    : DbContext(options)
{
    internal DbSet<AssetRow> Assets => Set<AssetRow>();
    internal DbSet<RelationshipRow> Relationships => Set<RelationshipRow>();

    /// <summary>
    /// The tenant for the current request. Read lazily through a property so the global query filter is
    /// evaluated per query, at execution time — never at construction. Startup creates a context outside
    /// any request scope (schema creation), and that path runs no tenant-scoped query, so it never
    /// touches this member.
    /// </summary>
    private string CurrentTenantId => requestContext.Tenant.TenantId;

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AssetRow>(e =>
        {
            e.ToTable("assets");
            e.HasKey(a => new { a.TenantId, a.Id });
            e.Property(a => a.Kind).HasMaxLength(32);
            e.Property(a => a.Name).HasMaxLength(256);
            e.Property(a => a.Lifecycle).HasMaxLength(16);
            e.HasIndex(a => new { a.TenantId, a.Kind });
            e.HasQueryFilter(a => a.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<RelationshipRow>(e =>
        {
            e.ToTable("relationships");
            e.HasKey(r => new { r.TenantId, r.Id });
            e.Property(r => r.Type).HasMaxLength(32);
            e.HasIndex(r => new { r.TenantId, r.FromId });
            e.HasQueryFilter(r => r.TenantId == CurrentTenantId);
        });
    }
}
