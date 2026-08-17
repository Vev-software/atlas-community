using System.Collections.Immutable;
using Vev.Atlas.Contracts;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Domain;

/// <summary>
/// Persistence port for the catalogue. Every operation is scoped by <see cref="TenantContext"/> —
/// isolation is a Fabric-owned security boundary, never optional (fabric#3, 05 §5). The runtime
/// swaps the implementation (SQLite dev, Postgres prod) behind this port; the domain does not care.
/// </summary>
public interface IAssetRepository
{
    /// <summary>List assets in the tenant, including their stable numeric ids.</summary>
    Task<ImmutableArray<CataloguedAsset>> ListCataloguedAssetsAsync(TenantContext tenant, AssetKind? kind, CancellationToken ct = default);

    /// <summary>List assets in the tenant, optionally filtered by kind.</summary>
    Task<ImmutableArray<Asset>> ListAssetsAsync(TenantContext tenant, AssetKind? kind, CancellationToken ct = default);

    /// <summary>Get one asset plus its stable numeric id, or null if it does not exist in the tenant.</summary>
    Task<CataloguedAsset?> GetCataloguedAssetAsync(TenantContext tenant, string id, CancellationToken ct = default);

    /// <summary>Get a single asset by id, or null if it does not exist in the tenant.</summary>
    Task<Asset?> GetAssetAsync(TenantContext tenant, string id, CancellationToken ct = default);

    /// <summary>True if an asset with the id exists in the tenant.</summary>
    Task<bool> AssetExistsAsync(TenantContext tenant, string id, CancellationToken ct = default);

    /// <summary>Reserve the next stable numeric id for a new asset in the tenant.</summary>
    Task<long> AllocateAssetNumericIdAsync(TenantContext tenant, CancellationToken ct = default);

    /// <summary>Insert a new asset. <paramref name="createdBy"/> is the principal id of the creator (atlas#76).</summary>
    Task AddAssetAsync(TenantContext tenant, Asset asset, long numericId, string? createdBy, CancellationToken ct = default);

    /// <summary>Replace an existing asset.</summary>
    Task UpdateAssetAsync(TenantContext tenant, Asset asset, CancellationToken ct = default);

    /// <summary>Delete an asset by id; returns false if it did not exist.</summary>
    Task<bool> DeleteAssetAsync(TenantContext tenant, string id, CancellationToken ct = default);

    /// <summary>List manual relationships in the tenant.</summary>
    Task<ImmutableArray<Relationship>> ListRelationshipsAsync(TenantContext tenant, CancellationToken ct = default);

    /// <summary>Insert a manual relationship.</summary>
    Task AddRelationshipAsync(TenantContext tenant, Relationship relationship, CancellationToken ct = default);

    /// <summary>Delete a relationship by id; returns false if it did not exist.</summary>
    Task<bool> DeleteRelationshipAsync(TenantContext tenant, string id, CancellationToken ct = default);

    /// <summary>
    /// Delete every relationship that touches an asset (as source or target) and return the ids removed.
    /// Used to keep the "both endpoints exist" invariant when an asset is deleted; the caller audits each id.
    /// </summary>
    Task<ImmutableArray<string>> DeleteRelationshipsForAssetAsync(TenantContext tenant, string assetId, CancellationToken ct = default);
}

/// <summary>One catalogued asset plus the stable numeric id assigned when it was created.</summary>
public sealed record CataloguedAsset(Asset Asset, long NumericId, string? CreatedBy);
