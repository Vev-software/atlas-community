using NetArchTest.Rules;
using Vev.Atlas.Domain;
using Xunit;

namespace Vev.Atlas.Architecture.Tests;

/// <summary>
/// Machine-enforced boundary rules (AGENTS.md §4 auto-reject, handbook 02 §7). These fail the build,
/// so the architecture is guaranteed rather than aspirational.
/// </summary>
public sealed class DependencyDirectionTests
{
    private static readonly System.Reflection.Assembly Domain = typeof(AssetService).Assembly;
    private static readonly System.Reflection.Assembly FabricAbstractions = typeof(Vev.Atlas.Fabric.TenantContext).Assembly;

    [Fact]
    public void Domain_does_not_depend_on_persistence_or_api()
    {
        // Everything points down: the domain must not know about the adapters that host it.
        var result = Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOnAny("Atlas.Persistence", "Atlas.Api", "Vev.Atlas.Persistence", "Vev.Atlas.Api")
            .GetResult();

        Assert.True(result.IsSuccessful, Explain(result));
    }

    [Fact]
    public void Domain_does_not_depend_on_a_specific_persistence_technology()
    {
        // Storage is behind the repository port (05 §2) — no EF Core leaking into the domain.
        var result = Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful, Explain(result));
    }

    [Fact]
    public void Fabric_shim_does_not_know_the_atlas_product_domain()
    {
        // Fabric must never know what an "asset" is (05 §3, AGENTS.md §1.1). The shim carries only
        // foundation contracts, so it cannot depend on atlas-contracts or the Atlas domain.
        var result = Types.InAssembly(FabricAbstractions)
            .ShouldNot()
            .HaveDependencyOnAny("Vev.Atlas.Contracts", "Vev.Atlas.Domain", "Atlas.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, Explain(result));
    }

    private static string Explain(TestResult result) =>
        result.IsSuccessful
            ? string.Empty
            : "Boundary violated by: " + string.Join(", ", result.FailingTypeNames ?? []);
}
