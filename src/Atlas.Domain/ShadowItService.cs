using System.Collections.Immutable;
using Vev.Atlas.Contracts;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Domain;

/// <summary>
/// Computes shadow-IT signals from the existing catalogue — no separate model required.
/// Three signals: unowned, unsanctioned, past-EOL. All derived from existing Asset fields.
/// </summary>
public sealed class ShadowItService(IRequestContextAccessor context, IAssetRepository repository)
{
    /// <summary>Compute shadow-IT summary over all assets in the current tenant.</summary>
    public async Task<ShadowItSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var assets = await repository.ListAssetsAsync(context.Tenant, kind: null, ct);
        return ComputeSummary(assets);
    }

    /// <summary>
    /// Filter assets by shadow-IT signals. Each flag uses OR logic — an asset matching any
    /// selected signal is included.
    /// </summary>
    public async Task<ImmutableArray<Asset>> GetFlaggedAssetsAsync(ShadowItFilter filter, CancellationToken ct = default)
    {
        var assets = await repository.ListAssetsAsync(context.Tenant, kind: null, ct);
        var flagged = assets.Where(a => Matches(a, filter)).ToImmutableArray();
        return flagged;
    }

    internal static ShadowItSummary ComputeSummary(IEnumerable<Asset> assets)
    {
        var enumerable = assets.ToImmutableArray();
        var total = enumerable.Length;
        var owned = enumerable.Count(HasOwnership);
        var unowned = total - owned;
        var sanctioned = enumerable.Count(IsSanctioned);
        var unsanctioned = total - sanctioned;
        var retired = enumerable.Count(a => a.Lifecycle == Lifecycle.Retired);
        var active = total - retired;

        return new ShadowItSummary(
            TotalAssets: total,
            OwnedAssets: owned,
            UnownedAssets: unowned,
            OwnershipCoveragePercent: total > 0 ? Math.Round(owned / (double)total * 100, 1) : 0,
            SanctionedAssets: sanctioned,
            UnsanctionedAssets: unsanctioned,
            ActiveAssets: active,
            RetiredAssets: retired);
    }

    internal static bool Matches(Asset asset, ShadowItFilter filter)
    {
        var match = false;

        if (filter.IncludeUnowned == true && !HasOwnership(asset))
            match = true;

        if (filter.IncludeUnsanctioned == true && !IsSanctioned(asset))
            match = true;

        if (filter.IncludePastEol == true && asset.Lifecycle == Lifecycle.Retired)
            match = true;

        return match;
    }

    /// <summary>
    /// An asset has ownership if:
    /// - kind == application AND application.businessOwner is non-empty, OR
    /// - kind == dataset AND dataset.owner is non-empty, OR
    /// - any kind with a tag matching owner:&lt;value&gt;
    /// </summary>
    internal static bool HasOwnership(Asset asset)
    {
        if (asset.Kind == AssetKind.Application && asset.Application is { } app &&
            !string.IsNullOrWhiteSpace(app.BusinessOwner))
            return true;

        if (asset.Kind == AssetKind.Dataset && asset.Dataset is { } ds &&
            !string.IsNullOrWhiteSpace(ds.Owner))
            return true;

        if (asset.Tags is { Length: > 0 })
        {
            foreach (var tag in asset.Tags)
            {
                if (tag is { Key: "owner" } && !string.IsNullOrWhiteSpace(tag.Value))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// An asset is sanctioned if it carries a tag matching sanctioned:true (case-insensitive).
    /// This is a Community Edition heuristic; formal governance state lives in the paid
    /// integration mapping capability.
    /// </summary>
    internal static bool IsSanctioned(Asset asset)
    {
        if (asset.Tags is { Length: > 0 })
        {
            foreach (var tag in asset.Tags)
            {
                if (tag.Key == "sanctioned" &&
                    string.Equals(tag.Value, "true", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}

/// <summary>Ownership coverage and shadow-IT signal counts for a tenant catalogue.</summary>
public sealed record ShadowItSummary(
    int TotalAssets,
    int OwnedAssets,
    int UnownedAssets,
    double OwnershipCoveragePercent,
    int SanctionedAssets,
    int UnsanctionedAssets,
    int ActiveAssets,
    int RetiredAssets);

/// <summary>Filter criteria for shadow-IT flagged assets. Flags use OR logic.</summary>
public sealed record ShadowItFilter(
    bool? IncludeUnowned,
    bool? IncludeUnsanctioned,
    bool? IncludePastEol);
