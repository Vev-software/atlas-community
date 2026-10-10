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
        IEnumerable<Asset> assets, IEnumerable<Relationship> relationships, LandscapeDigestScope scope) =>
        [.. Select(assets, relationships, scope).Select(entry => entry.Item)];

    /// <summary>One selected digest item, with the asset it came from (null for a synthesized vendor entry). The asset never leaves the builder.</summary>
    private sealed record Entry(Asset? Asset, LandscapeDigestItem Item);

    private static List<Entry> Select(IEnumerable<Asset> assets, IEnumerable<Relationship> relationships, LandscapeDigestScope scope)
    {
        var integrations = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var relationship in relationships.Where(r => r.Type == RelationshipType.ConnectsTo))
        {
            integrations[relationship.FromId] = integrations.GetValueOrDefault(relationship.FromId) + 1;
            integrations[relationship.ToId] = integrations.GetValueOrDefault(relationship.ToId) + 1;
        }

        var entries = new List<Entry>();
        var vendors = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Id, StringComparer.Ordinal))
        {
            if (MapKind(asset.Kind) is not { } kind || kind == DigestKind.Vendor || !scope.Kinds.Contains(kind) || !MatchesTags(asset, scope)) continue;
            var vendor = VendorOf(asset);
            if (vendor is not null) vendors.Add(vendor);
            var count = integrations.GetValueOrDefault(asset.Id);
            entries.Add(new Entry(asset, new LandscapeDigestItem(kind, Clip(asset.Name, MaxName), asset.Lifecycle, vendor,
                IntegrationCount: kind is DigestKind.System or DigestKind.Application && count > 0 ? count : null)));
        }

        // A vendor entry summarizes a vendor present among the items that qualified, so the tag filter applies to it too.
        if (scope.Kinds.Contains(DigestKind.Vendor))
        {
            entries.AddRange(vendors.Select(vendor => new Entry(null, new LandscapeDigestItem(DigestKind.Vendor, vendor, Lifecycle.Active))));
        }

        return entries;
    }

    /// <summary>The most edges a v2 digest may hold (the contract's limit).</summary>
    public const int MaxRelations = 20000;

    /// <summary>
    /// The v2 digest (relations stage 1): the same minimized items plus an opaque per-digest <c>ref</c>, a <c>layer</c> hint derived from the asset kind
    /// and the closed edges <c>runsOn</c> and <c>suppliedBy</c>. Only built when the sharer opted in to relations; with relations off the builder's v1
    /// output is used unchanged. An edge is emitted only between two items that are in the digest, so a kind or tag filter can never be bypassed by an
    /// edge, and nothing is read from a relationship beyond its two ends and its type. <c>connects-to</c> (stage 2), <c>depends-on</c>, <c>part-of</c>
    /// and <c>joins-on</c> are never exported.
    /// </summary>
    public static LandscapeDigestV2 BuildV2(
        IEnumerable<Asset> assets, IEnumerable<Relationship> relationships, LandscapeDigestScope scope,
        string digestId, string sourceInstanceId, int sequence, DateTimeOffset generatedAt)
    {
        var relationshipList = relationships as IReadOnlyCollection<Relationship> ?? [.. relationships];
        var entries = Select(assets, relationshipList, scope);

        // Refs are minted here, in the builder's stable order, and mean nothing outside this digest: never the asset id.
        var refs = new string[entries.Count];
        var items = ImmutableArray.CreateBuilder<LandscapeDigestItemV2>(entries.Count);
        var refOfAsset = new Dictionary<string, string>(StringComparer.Ordinal);
        var refOfVendor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Count; i++)
        {
            var (asset, item) = (entries[i].Asset, entries[i].Item);
            refs[i] = "c" + (i + 1);
            if (asset is not null) refOfAsset[asset.Id] = refs[i];
            else refOfVendor[item.Name] = refs[i];
            items.Add(new LandscapeDigestItemV2(refs[i], item.Kind, item.Name, item.Lifecycle, item.Vendor, item.PlannedChange, item.IntegrationCount, LayerOf(item.Kind)));
        }

        var kindByRef = items.ToDictionary(i => i.Ref, i => i.Kind, StringComparer.Ordinal);
        var edges = new SortedSet<(DigestRelationKind Kind, string From, string To)>(Comparer<(DigestRelationKind Kind, string From, string To)>.Create(CompareEdges));
        foreach (var relationship in relationshipList)
        {
            // `hosts` is the same fact as `runs-on` read the other way round, so both become one runsOn edge.
            var (systemId, platformId) = relationship.Type switch
            {
                RelationshipType.RunsOn => (relationship.FromId, relationship.ToId),
                RelationshipType.Hosts => (relationship.ToId, relationship.FromId),
                _ => ((string?)null, (string?)null),
            };
            if (systemId is null || platformId is null) continue;
            if (!refOfAsset.TryGetValue(systemId, out var from) || !refOfAsset.TryGetValue(platformId, out var to) || from == to) continue;
            if (kindByRef[from] is DigestKind.System or DigestKind.Application && kindByRef[to] == DigestKind.Platform) edges.Add((DigestRelationKind.RunsOn, from, to));
        }

        // A supplier edge follows the vendor name already in the digest; with vendor out of scope there is no vendor item and so no edge.
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Asset is null || entries[i].Item.Vendor is not { } vendor || !refOfVendor.TryGetValue(vendor, out var vendorRef)) continue;
            edges.Add((DigestRelationKind.SuppliedBy, refs[i], vendorRef));
        }

        var relationsScope = new LandscapeDigestScopeV2(scope.Kinds, scope.Tags, Relations: true);
        return new LandscapeDigestV2(digestId, generatedAt, sourceInstanceId, sequence, relationsScope, items.MoveToImmutable(),
            [.. edges.Take(MaxRelations).Select(e => new LandscapeDigestRelation(e.From, e.To, e.Kind))]);
    }

    private static int CompareEdges((DigestRelationKind Kind, string From, string To) a, (DigestRelationKind Kind, string From, string To) b)
    {
        if (a.Kind != b.Kind) return a.Kind.CompareTo(b.Kind);
        var from = string.CompareOrdinal(a.From, b.From);
        return from != 0 ? from : string.CompareOrdinal(a.To, b.To);
    }

    /// <summary>A digest classification derived from the digest kind, not an ArchiMate claim. Vendor entries carry none.</summary>
    private static DigestLayer? LayerOf(DigestKind kind) => kind switch
    {
        DigestKind.System or DigestKind.Application => DigestLayer.Application,
        DigestKind.Platform => DigestLayer.Infrastructure,
        _ => null,
    };

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
