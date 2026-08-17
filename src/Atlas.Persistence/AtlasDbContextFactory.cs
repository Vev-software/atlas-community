using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Persistence;

/// <summary>Design-time factory so EF migrations can be created without booting the whole web host.</summary>
public sealed class AtlasDbContextFactory : IDesignTimeDbContextFactory<AtlasDbContext>
{
    public AtlasDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AtlasDbContext>()
            .UseSqlite("Data Source=atlas.db")
            .Options;

        return new AtlasDbContext(options, new DesignTimeRequestContext());
    }

    private sealed class DesignTimeRequestContext : IRequestContextAccessor
    {
        public TenantContext Tenant => new("design-time");

        public PrincipalContext Principal => new("design-time", "Design time", ["AtlasArchitect"]);
    }
}
