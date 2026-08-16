using System.Collections.Immutable;
using System.Text;
using Vev.Atlas.Contracts;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Domain;

/// <summary>
/// Read-only export of a bounded catalogue slice as a portable context pack. The deterministic pack is
/// grounded strictly in the tenant's own catalogue data; the optional narrative overlay composes the
/// same grounded slice with the Fabric AI contract.
/// </summary>
public sealed class ContextPackService(
    IRequestContextAccessor context,
    IAuthorizer authorizer,
    IAuditSink audit,
    IAssetRepository repository,
    IAiAssistService aiAssist,
    TimeProvider clock)
{
    private static readonly ResourceId ContextPackResource = new("atlas:context-pack");

    /// <summary>
    /// Export the selected slice as a portable context pack. At least one asset id or relationship id
    /// must be provided; everything in the result is read-only and tenant-scoped.
    /// </summary>
    public async Task<ContextPackDocument> ExportAsync(
        IReadOnlyCollection<string> assetIds,
        IReadOnlyCollection<string> relationshipIds,
        string mode,
        CancellationToken ct = default)
    {
        AuthorizeRead();
        var pack = await BuildDeterministicAsync(assetIds, relationshipIds, ct);

        string? narrative = null;
        string? narrativeStatus = null;
        var source = "context-pack:deterministic";
        var action = AtlasCapabilities.ContextExport.Value;

        if (string.Equals(mode, ContextPackMode.Narrative, StringComparison.OrdinalIgnoreCase))
        {
            action = AtlasCapabilities.AiBrief.Value;
            var assist = aiAssist.Assist(new AiAssistRequest(
                context.Tenant,
                context.Principal,
                AtlasCapabilities.AiBrief,
                Purpose: "context-pack-brief",
                Grounding: pack.Markdown,
                Resource: ContextPackResource));

            source = assist.Source;
            if (assist.Configured)
            {
                narrative = assist.Message;
                narrativeStatus = "available";
            }
            else
            {
                narrativeStatus = "ai_not_configured";
            }
        }

        await audit.WriteAsync(new AuditEvent(
            TenantId: context.Tenant.TenantId,
            ActorPrincipalId: context.Principal.PrincipalId,
            Action: action,
            Resource: ContextPackAuditResource(mode, pack.Selection, pack.Assets.Count, pack.Relationships.Count).Value,
            OccurredAt: clock.GetUtcNow(),
            CorrelationId: Guid.NewGuid().ToString("N")), ct);

        return pack with
        {
            Mode = NormalizeMode(mode),
            Source = source,
            Narrative = narrative,
            NarrativeStatus = narrativeStatus
        };
    }

    /// <summary>
    /// Build the deterministic grounded slice without emitting usage/audit, so other read-only AI
    /// experiences can reuse the same bounded grounding logic without double-counting a context-pack export.
    /// </summary>
    public async Task<ContextPackDocument> BuildDeterministicAsync(
        IReadOnlyCollection<string> assetIds,
        IReadOnlyCollection<string> relationshipIds,
        CancellationToken ct = default)
    {
        AuthorizeRead();

        var normalizedAssetIds = assetIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
        var normalizedRelationshipIds = relationshipIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();

        if (normalizedAssetIds.IsDefaultOrEmpty && normalizedRelationshipIds.IsDefaultOrEmpty)
        {
            throw new CatalogueValidationException("Select at least one asset or relationship for the context pack.");
        }

        var tenant = context.Tenant;
        var allAssets = await repository.ListAssetsAsync(tenant, kind: null, ct);
        var allRelationships = await repository.ListRelationshipsAsync(tenant, ct);
        var assetsById = allAssets.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var relationshipsById = allRelationships.ToDictionary(r => r.Id, StringComparer.Ordinal);

        var selectedRelationships = normalizedRelationshipIds
            .Where(relationshipsById.ContainsKey)
            .Select(id => relationshipsById[id])
            .ToImmutableArray();

        var selectedAssetIds = normalizedAssetIds
            .Where(assetsById.ContainsKey)
            .Concat(selectedRelationships.SelectMany(r => new[] { r.FromId, r.ToId }))
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();

        if (selectedAssetIds.IsDefaultOrEmpty && selectedRelationships.IsDefaultOrEmpty)
        {
            throw new CatalogueValidationException("None of the selected assets or relationships exist in this tenant.");
        }

        var includedAssetIds = selectedAssetIds.ToHashSet(StringComparer.Ordinal);
        var includedRelationshipIds = selectedRelationships.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var relationship in selectedRelationships)
        {
            includedAssetIds.Add(relationship.FromId);
            includedAssetIds.Add(relationship.ToId);
        }

        if (selectedAssetIds.Length == 1)
        {
            foreach (var relationship in allRelationships.Where(r => r.FromId == selectedAssetIds[0] || r.ToId == selectedAssetIds[0]))
            {
                includedRelationshipIds.Add(relationship.Id);
                includedAssetIds.Add(relationship.FromId);
                includedAssetIds.Add(relationship.ToId);
            }
        }
        else
        {
            for (var i = 0; i < selectedAssetIds.Length; i++)
            {
                for (var j = i + 1; j < selectedAssetIds.Length; j++)
                {
                    var path = FindShortestPath(selectedAssetIds[i], selectedAssetIds[j], allRelationships);
                    foreach (var relationship in path)
                    {
                        includedRelationshipIds.Add(relationship.Id);
                        includedAssetIds.Add(relationship.FromId);
                        includedAssetIds.Add(relationship.ToId);
                    }
                }
            }
        }

        var includedAssets = OrderAssets(selectedAssetIds, includedAssetIds, assetsById);
        var includedRelationships = allRelationships
            .Where(r => includedRelationshipIds.Contains(r.Id))
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .ToImmutableArray();

        var selection = new ContextPackSelection(selectedAssetIds, normalizedRelationshipIds);
        var summary = BuildSummary(selection, includedAssets, includedRelationships);
        var markdown = RenderMarkdown(selection, summary, includedAssets, includedRelationships);

        return new ContextPackDocument(
            Mode: ContextPackMode.Deterministic,
            Source: "context-pack:deterministic",
            Selection: selection,
            Summary: summary,
            Assets: includedAssets,
            Relationships: includedRelationships,
            Markdown: markdown);
    }

    private void AuthorizeRead()
    {
        var decision = authorizer.Authorize(context.Tenant, context.Principal, AtlasActions.AssetRead, ContextPackResource);
        if (!decision.Allowed)
        {
            throw AccessDeniedException.FromAuthorization(decision, $"'{AtlasActions.AssetRead}' denied ({decision.ReasonCode}).");
        }
    }

    private static ImmutableArray<Asset> OrderAssets(
        ImmutableArray<string> selectedAssetIds,
        IReadOnlySet<string> includedAssetIds,
        IReadOnlyDictionary<string, Asset> assetsById)
    {
        var selected = selectedAssetIds
            .Where(id => assetsById.ContainsKey(id))
            .Select(id => assetsById[id]);

        var remainder = includedAssetIds
            .Where(id => assetsById.ContainsKey(id) && !selectedAssetIds.Contains(id, StringComparer.Ordinal))
            .Select(id => assetsById[id])
            .OrderBy(a => a.Name ?? a.Id, StringComparer.Ordinal);

        return [.. selected, .. remainder];
    }

    private static ImmutableArray<Relationship> FindShortestPath(
        string fromId,
        string toId,
        ImmutableArray<Relationship> relationships)
    {
        if (string.Equals(fromId, toId, StringComparison.Ordinal))
        {
            return [];
        }

        var queue = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { fromId };
        var previous = new Dictionary<string, (string Parent, Relationship Edge)>(StringComparer.Ordinal);
        queue.Enqueue(fromId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var relationship in relationships.Where(r => r.FromId == current || r.ToId == current))
            {
                var next = string.Equals(relationship.FromId, current, StringComparison.Ordinal)
                    ? relationship.ToId
                    : relationship.FromId;

                if (!visited.Add(next))
                {
                    continue;
                }

                previous[next] = (current, relationship);
                if (string.Equals(next, toId, StringComparison.Ordinal))
                {
                    return ReconstructPath(fromId, toId, previous);
                }

                queue.Enqueue(next);
            }
        }

        return [];
    }

    private static ImmutableArray<Relationship> ReconstructPath(
        string start,
        string end,
        IReadOnlyDictionary<string, (string Parent, Relationship Edge)> previous)
    {
        var edges = new List<Relationship>();
        var current = end;
        while (!string.Equals(current, start, StringComparison.Ordinal))
        {
            if (!previous.TryGetValue(current, out var step))
            {
                return [];
            }

            edges.Add(step.Edge);
            current = step.Parent;
        }

        edges.Reverse();
        return [.. edges];
    }

    private static string BuildSummary(
        ContextPackSelection selection,
        ImmutableArray<Asset> assets,
        ImmutableArray<Relationship> relationships)
    {
        var focus = selection.AssetIds.Count == 1
            ? $"focused on {selection.AssetIds[0]}"
            : $"spanning {selection.AssetIds.Count} selected assets";
        return $"Grounded context pack {focus}, with {assets.Length} asset(s) and {relationships.Length} relationship(s).";
    }

    private static string RenderMarkdown(
        ContextPackSelection selection,
        string summary,
        ImmutableArray<Asset> assets,
        ImmutableArray<Relationship> relationships)
    {
        var text = new StringBuilder();
        text.AppendLine("# Atlas Context Pack");
        text.AppendLine();
        text.AppendLine(summary);
        text.AppendLine();
        text.AppendLine("## Selection");
        text.AppendLine($"- Assets: {(selection.AssetIds.Count == 0 ? "none" : string.Join(", ", selection.AssetIds))}");
        text.AppendLine($"- Relationships: {(selection.RelationshipIds.Count == 0 ? "none" : string.Join(", ", selection.RelationshipIds))}");
        text.AppendLine();
        text.AppendLine("## Assets");

        foreach (var asset in assets)
        {
            text.AppendLine($"### {asset.Name ?? asset.Id}");
            text.AppendLine($"- ID: `{asset.Id}`");
            text.AppendLine($"- Kind: {asset.Kind}");
            text.AppendLine($"- Lifecycle: {asset.Lifecycle}");
            if (!string.IsNullOrWhiteSpace(asset.Description))
            {
                text.AppendLine($"- Notes: {asset.Description}");
            }

            if (!string.IsNullOrWhiteSpace(OwnerOf(asset)))
            {
                text.AppendLine($"- Owner: {OwnerOf(asset)}");
            }

            if (!string.IsNullOrWhiteSpace(PhysicalNameOf(asset)))
            {
                text.AppendLine($"- Physical name: {PhysicalNameOf(asset)}");
            }

            if (asset.Tags.Length > 0)
            {
                text.AppendLine($"- Tags: {string.Join(", ", asset.Tags.Select(t => t.Value is null ? t.Key : $"{t.Key}:{t.Value}"))}");
            }

            text.AppendLine();
        }

        text.AppendLine("## Relationships");
        if (relationships.Length == 0)
        {
            text.AppendLine("- None");
        }
        else
        {
            foreach (var relationship in relationships)
            {
                text.AppendLine($"- `{relationship.FromId}` {relationship.Type} `{relationship.ToId}`" +
                    (string.IsNullOrWhiteSpace(relationship.Description) ? string.Empty : $" — {relationship.Description}"));
            }
        }

        return text.ToString().TrimEnd();
    }

    private static string? OwnerOf(Asset asset) => asset.Application?.BusinessOwner ?? asset.Dataset?.Owner;

    private static string? PhysicalNameOf(Asset asset) => asset.Dataset?.PhysicalName;

    private static ResourceId ContextPackAuditResource(
        string mode,
        ContextPackSelection selection,
        int includedAssets,
        int includedRelationships) =>
        new($"atlas:context-pack?mode={NormalizeMode(mode)}&selectedAssets={selection.AssetIds.Count}&selectedRelationships={selection.RelationshipIds.Count}&includedAssets={includedAssets}&includedRelationships={includedRelationships}");

    private static string NormalizeMode(string mode) =>
        string.Equals(mode, ContextPackMode.Narrative, StringComparison.OrdinalIgnoreCase)
            ? ContextPackMode.Narrative
            : ContextPackMode.Deterministic;
}

/// <summary>A portable context pack over a bounded landscape slice.</summary>
public sealed record ContextPackDocument(
    string Mode,
    string Source,
    ContextPackSelection Selection,
    string Summary,
    IReadOnlyList<Asset> Assets,
    IReadOnlyList<Relationship> Relationships,
    string Markdown,
    string? Narrative = null,
    string? NarrativeStatus = null);

/// <summary>The user-selected focus that grounded the context pack.</summary>
public sealed record ContextPackSelection(
    IReadOnlyList<string> AssetIds,
    IReadOnlyList<string> RelationshipIds);

/// <summary>Wire modes for context pack export.</summary>
public static class ContextPackMode
{
    public const string Deterministic = "deterministic";
    public const string Narrative = "narrative";
}
