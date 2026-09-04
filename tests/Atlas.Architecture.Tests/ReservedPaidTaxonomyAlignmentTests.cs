using Vev.Atlas.Domain;
using Vev.Fabric.Contracts.Taxonomy;
using Xunit;

namespace Vev.Atlas.Architecture.Tests;

/// <summary>
/// The reserved-paid capability set and the canonical Fabric taxonomy must not drift apart.
///
/// Atlas historically carried a <i>second</i>, subtly different vocabulary of capability ids
/// (e.g. <c>atlas.portfolio.apm</c>) from the canonical Fabric taxonomy (<c>atlas.analysis.apm</c>).
/// That only "worked" because Community always denied — no grant snapshot exists in the free edition.
/// The moment a real signed snapshot flows (the entitlement-gated ui-extension mount), a Community gate
/// keyed on a non-canonical id can never be granted, because CommunityEntitlementService matches the
/// request capability value against a snapshot keyed on the canonical Fabric ids.
///
/// Fabric owns the source of truth. These tests fail closed if any reserved-paid id ceases to exist as
/// a <see cref="TaxonomyKind.Feature"/> capability marked <c>Reserved</c> in the Fabric catalog — which
/// is exactly what happens if someone reintroduces a bespoke Atlas string, or bumps the Fabric contract
/// to a version that drops or unreserves one of these seams.
/// </summary>
public sealed class ReservedPaidTaxonomyAlignmentTests
{
    private static readonly IReadOnlySet<string> FabricReservedCapabilityIds =
        Capabilities.All
            .Where(capability => capability is { Kind: TaxonomyKind.Feature, Reserved: true })
            .Select(capability => capability.Id)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Every_reserved_paid_capability_is_a_reserved_capability_in_the_Fabric_taxonomy()
    {
        var missing = AtlasCapabilities.ReservedPaid
            .Select(capability => capability.Value)
            .Where(id => !FabricReservedCapabilityIds.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "These reserved-paid capability ids are not Reserved features in the canonical Fabric " +
            "taxonomy, so a real entitlement snapshot could never grant them: " +
            string.Join(", ", missing) +
            ". Reconcile AtlasCapabilities onto the canonical Vev.Fabric.Contracts AtlasTaxonomy ids " +
            "(and publish the Fabric contract first if the id is newly reserved).");
    }

    [Fact]
    public void Reserved_paid_ids_use_the_canonical_Fabric_taxonomy_strings()
    {
        // A belt-and-braces check that the alias targets resolved to the expected canonical values,
        // independent of the Fabric package version this edition happens to pin.
        Assert.Equal("atlas.analysis.integration-map", AtlasCapabilities.IntegrationMapping.Value);
        Assert.Equal("atlas.analysis.eol", AtlasCapabilities.EndOfLifeTracking.Value);
        Assert.Equal("atlas.analysis.apm", AtlasCapabilities.PortfolioAnalysis.Value);
        Assert.Equal("atlas.analysis.roadmap", AtlasCapabilities.RoadmapGeneration.Value);
        Assert.Equal("atlas.ai.review", AtlasCapabilities.AiReview.Value);
        Assert.Equal("atlas.ai.generate", AtlasCapabilities.AiGenerate.Value);
        Assert.Equal("atlas.data.introspection", AtlasCapabilities.DataIntrospection.Value);
        Assert.Equal("atlas.data.overlap", AtlasCapabilities.DataOverlap.Value);
        Assert.Equal("atlas.data.quality", AtlasCapabilities.DataQuality.Value);
        Assert.Equal("atlas.export.archimate", AtlasCapabilities.ArchimateExport.Value);
    }
}
