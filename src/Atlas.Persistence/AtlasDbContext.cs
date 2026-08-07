using Microsoft.EntityFrameworkCore;

namespace Vev.Atlas.Persistence;

/// <summary>EF Core context for the Community catalogue. Kept deliberately small.</summary>
public sealed class AtlasDbContext(DbContextOptions<AtlasDbContext> options) : DbContext(options)
{
    internal DbSet<AssetRow> Assets => Set<AssetRow>();
    internal DbSet<RelationshipRow> Relationships => Set<RelationshipRow>();

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
        });

        modelBuilder.Entity<RelationshipRow>(e =>
        {
            e.ToTable("relationships");
            e.HasKey(r => new { r.TenantId, r.Id });
            e.Property(r => r.Type).HasMaxLength(32);
            e.HasIndex(r => new { r.TenantId, r.FromId });
        });
    }
}
