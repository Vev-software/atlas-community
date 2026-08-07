using System.Collections.Immutable;
using Vev.Atlas.Contracts;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Domain;

/// <summary>
/// The Community Edition catalogue service: hold and browse assets, manual relationships and tags.
/// Cataloguing only — analysis (integration mapping, EOL, APM, roadmap, AI review) is paid Atlas core
/// and lives elsewhere (handbook 11 §1). Every read is tenant-scoped; every write is authorized and
/// audited through the Fabric contracts.
/// </summary>
public sealed class AssetService(
    IRequestContextAccessor context,
    IAuthorizer authorizer,
    IAuditSink audit,
    IAssetRepository repository,
    TimeProvider clock)
{
    /// <summary>List assets in the current tenant, optionally filtered by kind.</summary>
    public Task<ImmutableArray<Asset>> ListAssetsAsync(AssetKind? kind, CancellationToken ct = default)
    {
        AuthorizeRead(AssetResource("*"));
        return repository.ListAssetsAsync(context.Tenant, kind, ct);
    }

    /// <summary>Get a single asset, or null if it does not exist in the current tenant.</summary>
    public Task<Asset?> GetAssetAsync(string id, CancellationToken ct = default)
    {
        AuthorizeRead(AssetResource(id));
        return repository.GetAssetAsync(context.Tenant, id, ct);
    }

    /// <summary>Create a new asset. Requires write authorization; emits an audit event.</summary>
    public async Task<Asset> CreateAssetAsync(Asset asset, CancellationToken ct = default)
    {
        var resource = AssetResource(asset.Id);
        AuthorizeWrite(resource);

        if (await repository.AssetExistsAsync(context.Tenant, asset.Id, ct))
        {
            throw new CatalogueConflictException($"Asset '{asset.Id}' already exists.");
        }

        await repository.AddAssetAsync(context.Tenant, asset, ct);
        await EmitAsync("atlas.asset.created", resource, ct);
        return asset;
    }

    /// <summary>Replace an existing asset. Requires write authorization; emits an audit event.</summary>
    public async Task<Asset?> UpdateAssetAsync(string id, Asset asset, CancellationToken ct = default)
    {
        if (!string.Equals(id, asset.Id, StringComparison.Ordinal))
        {
            throw new CatalogueValidationException("Asset id in the path and body must match.");
        }

        var resource = AssetResource(id);
        AuthorizeWrite(resource);

        if (!await repository.AssetExistsAsync(context.Tenant, id, ct))
        {
            return null;
        }

        await repository.UpdateAssetAsync(context.Tenant, asset, ct);
        await EmitAsync("atlas.asset.updated", resource, ct);
        return asset;
    }

    /// <summary>Delete an asset. Requires write authorization; emits an audit event when something was removed.</summary>
    public async Task<bool> DeleteAssetAsync(string id, CancellationToken ct = default)
    {
        var resource = AssetResource(id);
        AuthorizeWrite(resource);

        var removed = await repository.DeleteAssetAsync(context.Tenant, id, ct);
        if (removed)
        {
            await EmitAsync("atlas.asset.deleted", resource, ct);
        }

        return removed;
    }

    /// <summary>List manual relationships in the current tenant.</summary>
    public Task<ImmutableArray<Relationship>> ListRelationshipsAsync(CancellationToken ct = default)
    {
        AuthorizeRead(RelationshipResource("*"));
        return repository.ListRelationshipsAsync(context.Tenant, ct);
    }

    /// <summary>
    /// Read the whole tenant landscape — every asset plus every manual relationship — resolved into a
    /// single <see cref="LandscapeDocument"/>. This is the read model behind the read-only landscape
    /// browse/visualisation (atlas#6): still cataloguing, no analysis. Requires read authorization.
    /// </summary>
    public async Task<LandscapeDocument> GetLandscapeAsync(CancellationToken ct = default)
    {
        AuthorizeRead(AssetResource("*"));

        var tenant = context.Tenant;
        var assets = await repository.ListAssetsAsync(tenant, kind: null, ct);
        var relationships = await repository.ListRelationshipsAsync(tenant, ct);

        return new LandscapeDocument(
            Assets: assets,
            Relationships: relationships,
            ExportedAt: clock.GetUtcNow());
    }

    /// <summary>
    /// Create a manual relationship between two assets. Both endpoints must already exist — this is a
    /// held link, not a discovered one (handbook 11 §1). Requires write authorization; audited.
    /// </summary>
    public async Task<Relationship> CreateRelationshipAsync(Relationship relationship, CancellationToken ct = default)
    {
        var resource = RelationshipResource(relationship.Id);
        AuthorizeWrite(resource);

        var tenant = context.Tenant;
        if (!await repository.AssetExistsAsync(tenant, relationship.FromId, ct))
        {
            throw new CatalogueValidationException($"Relationship source '{relationship.FromId}' does not exist.");
        }

        if (!await repository.AssetExistsAsync(tenant, relationship.ToId, ct))
        {
            throw new CatalogueValidationException($"Relationship target '{relationship.ToId}' does not exist.");
        }

        await repository.AddRelationshipAsync(tenant, relationship, ct);
        await EmitAsync("atlas.relationship.created", resource, ct);
        return relationship;
    }

    /// <summary>Delete a manual relationship. Requires write authorization; audited when removed.</summary>
    public async Task<bool> DeleteRelationshipAsync(string id, CancellationToken ct = default)
    {
        var resource = RelationshipResource(id);
        AuthorizeWrite(resource);

        var removed = await repository.DeleteRelationshipAsync(context.Tenant, id, ct);
        if (removed)
        {
            await EmitAsync("atlas.relationship.deleted", resource, ct);
        }

        return removed;
    }

    private void AuthorizeRead(ResourceId resource) => Authorize(AtlasActions.AssetRead, resource);

    private void AuthorizeWrite(ResourceId resource) => Authorize(AtlasActions.AssetWrite, resource);

    private void Authorize(string action, ResourceId resource)
    {
        var decision = authorizer.Authorize(context.Tenant, context.Principal, action, resource);
        if (!decision.Allowed)
        {
            throw new AccessDeniedException(decision, $"'{action}' denied ({decision.ReasonCode}).");
        }
    }

    private ValueTask EmitAsync(string action, ResourceId resource, CancellationToken ct)
    {
        // No secrets, no customer content — only the actor, action and the resource identifier (E4/E5).
        var evt = new AuditEvent(
            TenantId: context.Tenant.TenantId,
            ActorPrincipalId: context.Principal.PrincipalId,
            Action: action,
            Resource: resource.Value,
            OccurredAt: clock.GetUtcNow(),
            CorrelationId: Guid.NewGuid().ToString("N"));
        return audit.WriteAsync(evt, ct);
    }

    private static ResourceId AssetResource(string id) => new($"atlas:asset/{id}");

    private static ResourceId RelationshipResource(string id) => new($"atlas:relationship/{id}");
}
