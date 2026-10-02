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
    internal DbSet<TargetVersionRow> TargetVersions => Set<TargetVersionRow>();
    internal DbSet<AssetRow> Assets => Set<AssetRow>();
    internal DbSet<RelationshipRow> Relationships => Set<RelationshipRow>();
    internal DbSet<AiModuleSettingsRow> AiModuleSettings => Set<AiModuleSettingsRow>();
    internal DbSet<DigestStateRow> DigestStates => Set<DigestStateRow>();
    internal DbSet<DigestKeyRow> DigestKeys => Set<DigestKeyRow>();
    internal DbSet<ConnectedConsumerRow> ConnectedConsumers => Set<ConnectedConsumerRow>();

    /// <summary>
    /// The tenant for the current request. Read lazily through a property so the global query filter is
    /// evaluated per query, at execution time — never at construction. Startup creates a context outside
    /// any request scope (schema creation), and that path runs no tenant-scoped query, so it never
    /// touches this member.
    /// </summary>
    private string CurrentTenantId
    {
        get
        {
            try
            {
                return requestContext.Tenant.TenantId;
            }
            catch (InvalidOperationException)
            {
                // No request context bound (e.g., during migrations or startup setup).
                // Return a sentinel that will never match any real tenant, ensuring safe isolation.
                return "___no_request_context___";
            }
        }
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TargetVersionRow>(e =>
        {
            e.ToTable("target_versions");
            e.HasKey(v => new { v.TenantId, v.Id });
            e.HasQueryFilter(v => v.TenantId == CurrentTenantId);
        });
        modelBuilder.Entity<AssetRow>(e =>
        {
            e.ToTable("assets");
            e.HasKey(a => new { a.TenantId, a.Id });
            e.Property(a => a.NumericId);
            e.Property(a => a.Kind).HasMaxLength(32);
            e.Property(a => a.Name).HasMaxLength(256);
            e.Property(a => a.Lifecycle).HasMaxLength(16);
            e.Property(a => a.CreatedBy).HasMaxLength(128);
            e.HasIndex(a => new { a.TenantId, a.Kind });
            e.HasIndex(a => new { a.TenantId, a.NumericId }).IsUnique();
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

        modelBuilder.Entity<DigestStateRow>(e =>
        {
            e.ToTable("digest_state");
            e.HasKey(r => r.TenantId);
            e.Property(r => r.SourceInstanceId).HasMaxLength(128);
            e.HasQueryFilter(r => r.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<ConnectedConsumerRow>(e =>
        {
            e.ToTable("connected_consumers");
            e.HasKey(r => new { r.TenantId, r.Id });
            e.Property(r => r.Id).HasMaxLength(64);
            e.Property(r => r.Name).HasMaxLength(80);
            e.Property(r => r.DestinationUrl).HasMaxLength(2048);
            e.Property(r => r.EnrollmentId).HasMaxLength(128);
            e.Property(r => r.State).HasMaxLength(16);
            e.Property(r => r.StopReason).HasMaxLength(64);
            e.Property(r => r.CreatedBy).HasMaxLength(128);
            e.Property(r => r.LastError).HasMaxLength(512);
            e.Property(r => r.LastContentFingerprint).HasMaxLength(64);
            e.HasIndex(r => r.State);
            e.HasQueryFilter(r => r.TenantId == CurrentTenantId);
        });

        // One signing key for the installation, so deliberately not tenant-scoped.
        modelBuilder.Entity<DigestKeyRow>(e =>
        {
            e.ToTable("digest_key");
            e.HasKey(r => r.Id);
            e.Property(r => r.KeyId).HasMaxLength(128);
        });

        modelBuilder.Entity<AiModuleSettingsRow>(e =>
        {
            e.ToTable("ai_module_settings");
            e.HasKey(r => r.TenantId);
            e.Property(r => r.Provider).HasMaxLength(32);
            e.Property(r => r.ConsentAcceptedBy).HasMaxLength(128);
            e.HasQueryFilter(r => r.TenantId == CurrentTenantId);
        });
    }
}
