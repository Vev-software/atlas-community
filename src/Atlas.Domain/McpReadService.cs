using System.Collections.Immutable;
using Vev.Atlas.Contracts;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Domain;

/// <summary>
/// Read-only catalogue access for the MCP surface. Reuses the same tenant, RBAC and audit seams as the
/// HTTP API, but records each tool call explicitly so remote agent access is never a silent read path.
/// </summary>
public sealed class McpReadService(
    IRequestContextAccessor context,
    IAuthorizer authorizer,
    IAuditSink audit,
    IAssetRepository repository,
    ContextPackService contextPackService,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<McpAssetRecord>> SearchAssetsAsync(string? query, AssetKind? kind, CancellationToken ct = default)
    {
        Authorize(AssetResource("*"));

        var assets = await repository.ListAssetsAsync(context.Tenant, kind, ct);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            assets = [.. assets.Where(asset => Matches(asset, term))];
        }

        assets = [.. assets.OrderBy(asset => asset.Name ?? asset.Id, StringComparer.Ordinal)];
        await EmitAsync("atlas.mcp.assets.list", SearchResource(kind, !string.IsNullOrWhiteSpace(query)), ct);
        return assets.Select(MapAsset).ToArray();
    }

    public async Task<McpAssetDetail?> GetAssetAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new CatalogueValidationException("Asset id is required.");
        }

        Authorize(AssetResource(id));

        var asset = await repository.GetAssetAsync(context.Tenant, id, ct);
        var relationships = await repository.ListRelationshipsAsync(context.Tenant, ct);
        await EmitAsync("atlas.mcp.asset.get", AssetResource(id), ct);

        return asset is null
            ? null
            : new McpAssetDetail(
                MapAsset(asset),
                [.. relationships
                    .Where(r => string.Equals(r.FromId, id, StringComparison.Ordinal) || string.Equals(r.ToId, id, StringComparison.Ordinal))
                    .OrderBy(r => r.Id, StringComparer.Ordinal)
                    .Select(MapRelationship)]);
    }

    public async Task<McpTraversalResult> TraverseRelationshipsAsync(
        IReadOnlyCollection<string> assetIds,
        int depth = 1,
        CancellationToken ct = default)
    {
        if (depth < 1 || depth > 4)
        {
            throw new CatalogueValidationException("Depth must be between 1 and 4.");
        }

        Authorize(RelationshipResource("*"));

        var normalizedAssetIds = assetIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
        if (normalizedAssetIds.IsDefaultOrEmpty)
        {
            throw new CatalogueValidationException("Select at least one asset id to traverse from.");
        }

        var tenant = context.Tenant;
        var assets = await repository.ListAssetsAsync(tenant, kind: null, ct);
        var relationships = await repository.ListRelationshipsAsync(tenant, ct);
        var assetsById = assets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);

        var frontier = new Queue<(string AssetId, int Depth)>();
        var visitedAssets = new HashSet<string>(StringComparer.Ordinal);
        var includedRelationshipIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var assetId in normalizedAssetIds.Where(assetsById.ContainsKey))
        {
            frontier.Enqueue((assetId, 0));
            visitedAssets.Add(assetId);
        }

        while (frontier.Count > 0)
        {
            var (assetId, currentDepth) = frontier.Dequeue();
            if (currentDepth >= depth)
            {
                continue;
            }

            foreach (var relationship in relationships.Where(r =>
                         string.Equals(r.FromId, assetId, StringComparison.Ordinal) ||
                         string.Equals(r.ToId, assetId, StringComparison.Ordinal)))
            {
                includedRelationshipIds.Add(relationship.Id);

                var nextAssetId = string.Equals(relationship.FromId, assetId, StringComparison.Ordinal)
                    ? relationship.ToId
                    : relationship.FromId;
                if (visitedAssets.Add(nextAssetId))
                {
                    frontier.Enqueue((nextAssetId, currentDepth + 1));
                }
            }
        }

        var orderedAssets = normalizedAssetIds
            .Where(visitedAssets.Contains)
            .Concat(visitedAssets.Where(id => !normalizedAssetIds.Contains(id, StringComparer.Ordinal)).OrderBy(id => assetsById[id].Name ?? id, StringComparer.Ordinal))
            .Select(id => assetsById[id])
            .ToImmutableArray();
        var orderedRelationships = relationships
            .Where(r => includedRelationshipIds.Contains(r.Id))
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .ToImmutableArray();

        await EmitAsync(
            "atlas.mcp.relationships.traverse",
            new ResourceId($"atlas:mcp/relationships?selectedAssets={normalizedAssetIds.Length}&includedAssets={orderedAssets.Length}&includedRelationships={orderedRelationships.Length}&depth={depth}"),
            ct);

        return new McpTraversalResult(
            orderedAssets.Select(MapAsset).ToArray(),
            orderedRelationships.Select(MapRelationship).ToArray(),
            depth);
    }

    public async Task<McpContextPack> ExportContextPackAsync(
        IReadOnlyCollection<string> assetIds,
        IReadOnlyCollection<string> relationshipIds,
        CancellationToken ct = default)
    {
        var pack = await contextPackService.ExportAsync(assetIds, relationshipIds, ContextPackMode.Deterministic, ct);
        await EmitAsync(
            "atlas.mcp.context-pack.exported",
            new ResourceId($"atlas:mcp/context-pack?selectedAssets={pack.Selection.AssetIds.Count}&selectedRelationships={pack.Selection.RelationshipIds.Count}&includedAssets={pack.Assets.Count}&includedRelationships={pack.Relationships.Count}"),
            ct);
        return new McpContextPack(
            pack.Mode,
            pack.Source,
            pack.Summary,
            pack.Markdown,
            pack.Narrative,
            pack.NarrativeStatus,
            pack.Selection.AssetIds.ToArray(),
            pack.Selection.RelationshipIds.ToArray(),
            pack.Assets.Select(MapAsset).ToArray(),
            pack.Relationships.Select(MapRelationship).ToArray());
    }

    private void Authorize(ResourceId resource)
    {
        var decision = authorizer.Authorize(context.Tenant, context.Principal, AtlasActions.AssetRead, resource);
        if (!decision.Allowed)
        {
            throw AccessDeniedException.FromAuthorization(decision, $"'{AtlasActions.AssetRead}' denied ({decision.ReasonCode}).");
        }
    }

    private ValueTask EmitAsync(string action, ResourceId resource, CancellationToken ct) =>
        audit.WriteAsync(new AuditEvent(
            TenantId: context.Tenant.TenantId,
            ActorPrincipalId: context.Principal.PrincipalId,
            Action: action,
            Resource: resource.Value,
            OccurredAt: clock.GetUtcNow(),
            CorrelationId: Guid.NewGuid().ToString("N")), ct);

    private static bool Matches(Asset asset, string term) =>
        Contains(asset.Id, term) ||
        Contains(asset.Name, term) ||
        Contains(asset.Description, term) ||
        Contains(asset.Application?.BusinessOwner, term) ||
        Contains(asset.Dataset?.Owner, term) ||
        Contains(asset.Dataset?.PhysicalName, term) ||
        asset.Tags.Any(tag =>
            Contains(tag.Key, term) ||
            Contains(tag.Value, term) ||
            Contains(tag.Value is null ? tag.Key : $"{tag.Key}:{tag.Value}", term));

    private static bool Contains(string? value, string term) =>
        value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;

    private static McpAssetRecord MapAsset(Asset asset) =>
        new(
            asset.Id,
            KindWire(asset.Kind),
            asset.Name,
            asset.Lifecycle.ToString(),
            asset.Description,
            OwnerOf(asset),
            asset.Dataset?.PhysicalName,
            asset.Tags.Select(tag => tag.Value is null ? tag.Key : $"{tag.Key}:{tag.Value}").ToArray());

    private static McpRelationshipRecord MapRelationship(Relationship relationship) =>
        new(
            relationship.Id,
            relationship.FromId,
            relationship.ToId,
            relationship.Type.ToString(),
            relationship.Description);

    private static string? OwnerOf(Asset asset) => asset.Application?.BusinessOwner ?? asset.Dataset?.Owner;

    private static ResourceId AssetResource(string id) => new($"atlas:asset/{id}");

    private static ResourceId RelationshipResource(string id) => new($"atlas:relationship/{id}");

    private static ResourceId SearchResource(AssetKind? kind, bool hasQuery) =>
        new($"atlas:mcp/assets?kind={(kind is null ? "all" : KindWire(kind.Value))}&query={(hasQuery ? "present" : "none")}");

    private static string KindWire(AssetKind kind) =>
        System.Text.Json.JsonSerializer.Serialize(kind, AtlasContracts.SerializerOptions).Trim('"');
}

/// <summary>A single asset plus the relationships that directly touch it.</summary>
public sealed record McpAssetDetail(McpAssetRecord Asset, IReadOnlyList<McpRelationshipRecord> Relationships);

/// <summary>A schema-friendly MCP view of an asset.</summary>
public sealed record McpAssetRecord(
    string Id,
    string Kind,
    string? Name,
    string Lifecycle,
    string? Description,
    string? Owner,
    string? PhysicalName,
    IReadOnlyList<string> Tags);

/// <summary>A schema-friendly MCP view of a relationship.</summary>
public sealed record McpRelationshipRecord(
    string Id,
    string FromId,
    string ToId,
    string Type,
    string? Description);

/// <summary>A bounded relationship walk from one or more starting assets.</summary>
public sealed record McpTraversalResult(
    IReadOnlyList<McpAssetRecord> Assets,
    IReadOnlyList<McpRelationshipRecord> Relationships,
    int Depth);

/// <summary>A schema-friendly MCP view of a deterministic context-pack export.</summary>
public sealed record McpContextPack(
    string Mode,
    string Source,
    string Summary,
    string Markdown,
    string? Narrative,
    string? NarrativeStatus,
    IReadOnlyList<string> SelectedAssetIds,
    IReadOnlyList<string> SelectedRelationshipIds,
    IReadOnlyList<McpAssetRecord> Assets,
    IReadOnlyList<McpRelationshipRecord> Relationships);
