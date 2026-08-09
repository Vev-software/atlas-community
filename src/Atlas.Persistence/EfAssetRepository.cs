using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vev.Atlas.Contracts;
using Vev.Atlas.Domain;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Persistence;

/// <summary>EF Core implementation of <see cref="IAssetRepository"/>. Every query is filtered by tenant.</summary>
public sealed class EfAssetRepository(AtlasDbContext db) : IAssetRepository
{
    private static readonly JsonSerializerOptions Json = AtlasContracts.SerializerOptions;

    public async Task<ImmutableArray<Asset>> ListAssetsAsync(TenantContext tenant, AssetKind? kind, CancellationToken ct = default)
    {
        var query = db.Assets.AsNoTracking().Where(a => a.TenantId == tenant.TenantId);
        if (kind is { } k)
        {
            var wire = Wire(k);
            query = query.Where(a => a.Kind == wire);
        }

        var rows = await query.OrderBy(a => a.Name).ToListAsync(ct);
        return [.. rows.Select(FromRow)];
    }

    public async Task<Asset?> GetAssetAsync(TenantContext tenant, string id, CancellationToken ct = default)
    {
        var row = await db.Assets.AsNoTracking()
            .FirstOrDefaultAsync(a => a.TenantId == tenant.TenantId && a.Id == id, ct);
        return row is null ? null : FromRow(row);
    }

    public Task<bool> AssetExistsAsync(TenantContext tenant, string id, CancellationToken ct = default) =>
        db.Assets.AnyAsync(a => a.TenantId == tenant.TenantId && a.Id == id, ct);

    public async Task AddAssetAsync(TenantContext tenant, Asset asset, CancellationToken ct = default)
    {
        db.Assets.Add(ToRow(tenant, asset));
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAssetAsync(TenantContext tenant, Asset asset, CancellationToken ct = default)
    {
        var row = await db.Assets.FirstOrDefaultAsync(a => a.TenantId == tenant.TenantId && a.Id == asset.Id, ct)
            ?? throw new InvalidOperationException($"Asset '{asset.Id}' not found for update.");

        row.Kind = Wire(asset.Kind);
        row.Name = asset.Name;
        row.Lifecycle = Wire(asset.Lifecycle);
        row.DocumentJson = JsonSerializer.Serialize(asset, Json);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAssetAsync(TenantContext tenant, string id, CancellationToken ct = default)
    {
        var deleted = await db.Assets
            .Where(a => a.TenantId == tenant.TenantId && a.Id == id)
            .ExecuteDeleteAsync(ct);
        return deleted > 0;
    }

    public async Task<ImmutableArray<Relationship>> ListRelationshipsAsync(TenantContext tenant, CancellationToken ct = default)
    {
        var rows = await db.Relationships.AsNoTracking()
            .Where(r => r.TenantId == tenant.TenantId)
            .OrderBy(r => r.Id)
            .ToListAsync(ct);
        return [.. rows.Select(FromRow)];
    }

    public async Task AddRelationshipAsync(TenantContext tenant, Relationship relationship, CancellationToken ct = default)
    {
        db.Relationships.Add(new RelationshipRow
        {
            TenantId = tenant.TenantId,
            Id = relationship.Id,
            FromId = relationship.FromId,
            ToId = relationship.ToId,
            Type = Wire(relationship.Type),
            Description = relationship.Description
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteRelationshipAsync(TenantContext tenant, string id, CancellationToken ct = default)
    {
        var deleted = await db.Relationships
            .Where(r => r.TenantId == tenant.TenantId && r.Id == id)
            .ExecuteDeleteAsync(ct);
        return deleted > 0;
    }

    public async Task<ImmutableArray<string>> DeleteRelationshipsForAssetAsync(TenantContext tenant, string assetId, CancellationToken ct = default)
    {
        var ids = await db.Relationships
            .Where(r => r.TenantId == tenant.TenantId && (r.FromId == assetId || r.ToId == assetId))
            .Select(r => r.Id)
            .ToListAsync(ct);

        if (ids.Count > 0)
        {
            await db.Relationships
                .Where(r => r.TenantId == tenant.TenantId && (r.FromId == assetId || r.ToId == assetId))
                .ExecuteDeleteAsync(ct);
        }

        return [.. ids];
    }

    private static AssetRow ToRow(TenantContext tenant, Asset asset) => new()
    {
        TenantId = tenant.TenantId,
        Id = asset.Id,
        Kind = Wire(asset.Kind),
        Name = asset.Name,
        Lifecycle = Wire(asset.Lifecycle),
        DocumentJson = JsonSerializer.Serialize(asset, Json)
    };

    private static Asset FromRow(AssetRow row) =>
        JsonSerializer.Deserialize<Asset>(row.DocumentJson, Json)
        ?? throw new InvalidOperationException($"Corrupt asset document for '{row.Id}'.");

    private static Relationship FromRow(RelationshipRow row) =>
        new(row.Id, row.FromId, row.ToId, WireToRelationshipType(row.Type), row.Description);

    // Single source of truth for enum wire values: the contract's own serialization.
    private static string Wire<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonSerializer.Serialize(value, Json).Trim('"');

    private static RelationshipType WireToRelationshipType(string wire) =>
        JsonSerializer.Deserialize<RelationshipType>($"\"{wire}\"", Json);
}
