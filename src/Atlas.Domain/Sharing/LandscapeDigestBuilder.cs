using System.Collections.Immutable;
using Vev.Atlas.Contracts;

namespace Vev.Atlas.Domain.Sharing;

/// <summary>
/// Builds the minimized landscape share digest (atlas-contracts landscape digest v1) from the catalogue.
/// <para>
/// Minimization is enforced <b>here, when the digest is built</b>, and not only by the schema: the builder reads
/// exactly a name, a kind, a lifecycle, tags (only to filter), an optional vendor or provider, and the number of
/// <c>connects-to</c> relationships. It never reads descriptions, hostnames, environments, operating systems,
/// endpoints, locations, owners, attachments, people or data-layer assets, so a landscape that holds them still
/// cannot leak them through a digest. Anything new an asset gains is excluded until it is added here on purpose.
/// </para>
/// </summary>
public static class LandscapeDigestBuilder
{
    /// <summary>The digest kinds a sharer can choose from.</summary>
    public static readonly ImmutableArray<DigestKind> AllKinds = [DigestKind.System, DigestKind.Application, DigestKind.Platform, DigestKind.Vendor];

    private const int MaxName = 256;
    private const int MaxVendor = 128;

    /// <summary>The digest kind of a catalogue kind. Null: the kind is never shared (data layer, integrations, business processes).</summary>
    public static DigestKind? MapKind(AssetKind kind) => kind switch
    {
        AssetKind.System => DigestKind.System,
        AssetKind.Application => DigestKind.Application,
        // Server, infrastructure and the AI substrate fold into platform; the contract has no narrower vocabulary.
        AssetKind.Server or AssetKind.Infrastructure or AssetKind.AiService or AssetKind.AiModel => DigestKind.Platform,
        _ => null,
    };

    /// <summary>The items a digest with this scope would hold, in a stable order.</summary>
    public static ImmutableArray<LandscapeDigestItem> BuildItems(
        IEnumerable<Asset> assets, IEnumerable<Relationship> relationships, LandscapeDigestScope scope)
    {
        var integrations = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var relationship in relationships.Where(r => r.Type == RelationshipType.ConnectsTo))
        {
            integrations[relationship.FromId] = integrations.GetValueOrDefault(relationship.FromId) + 1;
            integrations[relationship.ToId] = integrations.GetValueOrDefault(relationship.ToId) + 1;
        }

        var items = new List<LandscapeDigestItem>();
        var vendors = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Id, StringComparer.Ordinal))
        {
            if (MapKind(asset.Kind) is not { } kind || kind == DigestKind.Vendor || !scope.Kinds.Contains(kind) || !MatchesTags(asset, scope)) continue;
            var vendor = VendorOf(asset);
            if (vendor is not null) vendors.Add(vendor);
            var count = integrations.GetValueOrDefault(asset.Id);
            items.Add(new LandscapeDigestItem(kind, Clip(asset.Name, MaxName), asset.Lifecycle, vendor,
                IntegrationCount: kind is DigestKind.System or DigestKind.Application && count > 0 ? count : null));
        }

        // A vendor entry summarizes a vendor present among the items that qualified, so the tag filter applies to it too.
        if (scope.Kinds.Contains(DigestKind.Vendor))
        {
            items.AddRange(vendors.Select(vendor => new LandscapeDigestItem(DigestKind.Vendor, vendor, Lifecycle.Active)));
        }

        return [.. items];
    }

    public static LandscapeDigest Build(
        IEnumerable<Asset> assets, IEnumerable<Relationship> relationships, LandscapeDigestScope scope,
        string digestId, string sourceInstanceId, int sequence, DateTimeOffset generatedAt) =>
        new(digestId, generatedAt, sourceInstanceId, sequence, scope, BuildItems(assets, relationships, scope));

    /// <summary>Tag filters are OR within the list and AND with the kind filter; no filter means every tag.</summary>
    private static bool MatchesTags(Asset asset, LandscapeDigestScope scope) =>
        scope.Tags.Length == 0 || asset.Tags.Any(tag => scope.Tags.Any(wanted => TagEquals(wanted, tag)));

    private static bool TagEquals(Tag a, Tag b) =>
        string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Value, b.Value, StringComparison.OrdinalIgnoreCase);

    /// <summary>The vendor of an application, or the provider of an AI service or model. Nothing else names an organization.</summary>
    private static string? VendorOf(Asset asset)
    {
        var vendor = asset.Kind switch
        {
            AssetKind.Application => asset.Application?.Vendor,
            AssetKind.AiService => asset.AiService?.Provider,
            AssetKind.AiModel => asset.AiModel?.Provider,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(vendor) ? null : Clip(vendor.Trim(), MaxVendor);
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
