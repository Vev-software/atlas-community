using System.ComponentModel;
using ModelContextProtocol.Server;
using Vev.Atlas.Contracts;
using Vev.Atlas.Domain;

namespace Vev.Atlas.Api;

/// <summary>
/// Read-only MCP tool surface over the tenant catalogue. The tools stay grounded in the same catalogue
/// services as the HTTP API and never open a write path.
/// </summary>
[McpServerToolType]
public sealed class AtlasMcpTools(McpReadService service)
{
    [McpServerTool(Name = "atlas_list_assets", UseStructuredContent = true)]
    [Description("List or search catalogue assets visible to the current tenant principal.")]
    public Task<IReadOnlyList<McpAssetRecord>> ListAssetsAsync(
        [Description("Optional free-text search over id, name, notes, owner, physical name and tags.")] string? query = null,
        [Description("Optional asset kind filter using the atlas-contracts wire names, for example application, server or dataset.")] string? kind = null,
        CancellationToken ct = default) =>
        ListAssetsCoreAsync(query, kind, ct);

    [McpServerTool(Name = "atlas_get_asset", UseStructuredContent = true)]
    [Description("Fetch a single asset plus the direct relationships that touch it.")]
    public Task<McpAssetDetail?> GetAssetAsync(
        [Description("Stable asset id.")] string id,
        CancellationToken ct = default) =>
        service.GetAssetAsync(id, ct);

    [McpServerTool(Name = "atlas_traverse_relationships", UseStructuredContent = true)]
    [Description("Traverse relationships outward from one or more starting assets, up to a bounded depth.")]
    public Task<McpTraversalResult> TraverseRelationshipsAsync(
        [Description("One or more starting asset ids.")] string[] assetIds,
        [Description("Traversal depth from 1 to 4. Defaults to 1.")] int depth = 1,
        CancellationToken ct = default) =>
        service.TraverseRelationshipsAsync(assetIds, depth, ct);

    [McpServerTool(Name = "atlas_export_context_pack", UseStructuredContent = true)]
    [Description("Export a deterministic, grounded context pack for a selected slice of the landscape.")]
    public Task<McpContextPack> ExportContextPackAsync(
        [Description("Selected asset ids.")] string[] assetIds,
        [Description("Optional selected relationship ids.")] string[]? relationshipIds = null,
        CancellationToken ct = default) =>
        service.ExportContextPackAsync(assetIds, relationshipIds ?? [], ct);

    private async Task<IReadOnlyList<McpAssetRecord>> ListAssetsCoreAsync(string? query, string? kind, CancellationToken ct)
    {
        var parsedKind = ParseKind(kind);
        return await service.SearchAssetsAsync(query, parsedKind, ct);
    }

    private static AssetKind? ParseKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<AssetKind>($"\"{kind}\"", AtlasContracts.SerializerOptions);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new CatalogueValidationException($"Unknown asset kind '{kind}'.");
        }
    }
}
