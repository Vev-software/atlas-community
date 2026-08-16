using System.Collections.Immutable;
using Vev.Atlas.Contracts;
using Vev.Atlas.Domain.Portability;
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
    /// <summary>
    /// Describe what the current principal may do in the catalogue, so a pure API client (the landscape
    /// UI) can show author affordances only to author-capable users and keep its badge honest. This is a
    /// self-describing capability probe: it asks the Fabric authorizer for the real decision without
    /// performing — or implying — any write. It is not itself gated, so a read-only principal can learn
    /// that it is read-only.
    /// </summary>
    public CatalogueCapabilities DescribeCapabilities()
    {
        var decision = authorizer.Authorize(
            context.Tenant, context.Principal, AtlasActions.AssetWrite, AssetResource("*"));
        return new CatalogueCapabilities(CanAuthor: decision.Allowed);
    }

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

    /// <summary>
    /// Delete an asset. Requires write authorization; emits an audit event when something was removed.
    /// Cascades to the asset's manual relationships so the "both endpoints exist" invariant that
    /// <see cref="CreateRelationshipAsync"/> enforces on create is not left broken on delete — otherwise a
    /// relationship would dangle against a missing asset and leak into the portable landscape document. Each
    /// removed relationship is audited in its own right.
    /// </summary>
    public async Task<bool> DeleteAssetAsync(string id, CancellationToken ct = default)
    {
        var resource = AssetResource(id);
        AuthorizeWrite(resource);

        var removed = await repository.DeleteAssetAsync(context.Tenant, id, ct);
        if (removed)
        {
            var orphaned = await repository.DeleteRelationshipsForAssetAsync(context.Tenant, id, ct);
            foreach (var relationshipId in orphaned)
            {
                await EmitAsync("atlas.relationship.deleted", RelationshipResource(relationshipId), ct);
            }

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
        return await ComposeLandscapeAsync(ct);
    }

    /// <summary>
    /// Export the whole tenant landscape as a portable document (customer-owned export, issue #12). A
    /// full-map export is the single highest-value reconnaissance read, so it is a deliberate, elevated,
    /// recorded action rather than a bulk read that leaves no trace (atlas#36): it requires the
    /// <see cref="AtlasActions.LandscapeExport"/> authorization — a read-only customer is denied — and it
    /// emits exactly one audit record capturing the actor, tenant, time, scope and format. Rendering to a
    /// concrete byte format stays at the edge (the format-adapter seam); <paramref name="format"/> is the
    /// resolved format id, recorded in the audit trail.
    /// </summary>
    public async Task<LandscapeDocument> ExportLandscapeAsync(string format, CancellationToken ct = default)
    {
        Authorize(AtlasActions.LandscapeExport, LandscapeResource);
        var landscape = await ComposeLandscapeAsync(ct);
        await EmitAsync("atlas.landscape.exported", ExportResource(format), ct);
        return landscape;
    }

    private async Task<LandscapeDocument> ComposeLandscapeAsync(CancellationToken ct)
    {
        var tenant = context.Tenant;
        var assets = await repository.ListAssetsAsync(tenant, kind: null, ct);
        var relationships = await repository.ListRelationshipsAsync(tenant, ct);

        return new LandscapeDocument(
            Assets: assets,
            Relationships: relationships,
            ExportedAt: clock.GetUtcNow(),
            Generator: LandscapeProvenance.Generator);
    }

    /// <summary>
    /// Apply a portable <see cref="ImportBundle"/> into the current tenant's catalogue — the import
    /// half of customer-owned portability (issue #12, handbook 11 §2-3). This is the core apply seam:
    /// it takes the <b>canonical contract form</b>, so any community format adapter that translates its
    /// format to an <see cref="ImportBundle"/> composes without touching this logic.
    ///
    /// <para>Every imported asset is matched by a stable catalogue id — its explicit
    /// <see cref="ImportAsset.Id"/> when given, otherwise its <see cref="ImportAsset.ExternalId"/> — so
    /// re-importing the same bundle is idempotent (Merge upserts; Replace makes the tenant match the
    /// bundle). Relationship endpoints must resolve to an asset in the bundle or already in the
    /// catalogue; an unresolved reference is rejected before anything is written. Requires write
    /// authorization; emits an audit event.</para>
    /// </summary>
    public async Task<ImportResult> ImportLandscapeAsync(ImportBundle bundle, CancellationToken ct = default)
    {
        // A bulk write across the whole tenant catalogue: one write authorization for the operation.
        AuthorizeWrite(AssetResource("*"));
        var tenant = context.Tenant;

        // --- Resolve + validate first; do not mutate anything until the whole bundle is known good. ---

        // Map every reference an asset carries (its id and/or externalId) to the stable catalogue id.
        var referenceToId = new Dictionary<string, string>(StringComparer.Ordinal);
        var resolvedAssets = new List<Asset>(bundle.Assets.Length);
        foreach (var imported in bundle.Assets)
        {
            var id = imported.Id ?? imported.ExternalId
                ?? throw new CatalogueValidationException(
                    $"Imported asset '{imported.Name}' must carry an id or an externalId.");

            if (imported.Id is not null) referenceToId[imported.Id] = id;
            if (imported.ExternalId is not null) referenceToId[imported.ExternalId] = id;

            resolvedAssets.Add(new Asset(
                id, imported.Kind, imported.Name, imported.Lifecycle, imported.Description,
                imported.Tags, imported.Application, imported.Server, imported.Infrastructure));
        }

        // Resolve relationship endpoints against the bundle first, then the existing catalogue. An
        // endpoint that resolves to neither is a hard error — reject the whole bundle, write nothing.
        var resolvedRelationships = new List<Relationship>(bundle.Relationships.Length);
        foreach (var relationship in bundle.Relationships)
        {
            var from = await ResolveEndpointAsync(tenant, relationship.FromRef, referenceToId, ct);
            var to = await ResolveEndpointAsync(tenant, relationship.ToRef, referenceToId, ct);

            // Deterministic id when the bundle does not supply one, so re-import stays idempotent.
            var id = relationship.Id ?? $"{from}~{relationship.Type}~{to}";
            resolvedRelationships.Add(new Relationship(id, from, to, relationship.Type, relationship.Description));
        }

        // --- Apply. ---

        var assetsDeleted = 0;
        if (bundle.Mode == ImportMode.Replace)
        {
            assetsDeleted = await PruneToBundleAsync(tenant, resolvedAssets, resolvedRelationships, ct);
        }

        var (created, updated) = await UpsertAssetsAsync(tenant, resolvedAssets, ct);
        await UpsertRelationshipsAsync(tenant, resolvedRelationships, ct);

        await EmitAsync("atlas.landscape.imported", AssetResource("*"), ct);

        return new ImportResult(bundle.Mode, created, updated, assetsDeleted, resolvedRelationships.Count);
    }

    private async Task<string> ResolveEndpointAsync(
        TenantContext tenant, string reference, IReadOnlyDictionary<string, string> referenceToId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new CatalogueValidationException("A relationship is missing a fromRef or toRef endpoint.");
        }

        if (referenceToId.TryGetValue(reference, out var mapped))
        {
            return mapped;
        }

        if (await repository.AssetExistsAsync(tenant, reference, ct))
        {
            return reference;
        }

        throw new CatalogueValidationException(
            $"Relationship references unknown asset '{reference}' — not in the bundle or the catalogue.");
    }

    private async Task<(int Created, int Updated)> UpsertAssetsAsync(
        TenantContext tenant, IReadOnlyList<Asset> assets, CancellationToken ct)
    {
        var created = 0;
        var updated = 0;
        foreach (var asset in assets)
        {
            if (await repository.AssetExistsAsync(tenant, asset.Id, ct))
            {
                await repository.UpdateAssetAsync(tenant, asset, ct);
                updated++;
            }
            else
            {
                await repository.AddAssetAsync(tenant, asset, ct);
                created++;
            }
        }

        return (created, updated);
    }

    private async Task UpsertRelationshipsAsync(
        TenantContext tenant, IReadOnlyList<Relationship> relationships, CancellationToken ct)
    {
        // The repository port carries no relationship update, so upsert is delete-then-add by id.
        var existing = (await repository.ListRelationshipsAsync(tenant, ct))
            .Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var relationship in relationships)
        {
            if (existing.Contains(relationship.Id))
            {
                await repository.DeleteRelationshipAsync(tenant, relationship.Id, ct);
            }

            await repository.AddRelationshipAsync(tenant, relationship, ct);
        }
    }

    private async Task<int> PruneToBundleAsync(
        TenantContext tenant, IReadOnlyList<Asset> assets, IReadOnlyList<Relationship> relationships, CancellationToken ct)
    {
        var keepAssets = assets.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var keepRelationships = relationships.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

        // Drop relationships not in the bundle first, then the assets they might have pointed at.
        foreach (var relationship in await repository.ListRelationshipsAsync(tenant, ct))
        {
            if (!keepRelationships.Contains(relationship.Id))
            {
                await repository.DeleteRelationshipAsync(tenant, relationship.Id, ct);
            }
        }

        var deleted = 0;
        foreach (var asset in await repository.ListAssetsAsync(tenant, kind: null, ct))
        {
            if (!keepAssets.Contains(asset.Id) && await repository.DeleteAssetAsync(tenant, asset.Id, ct))
            {
                deleted++;
            }
        }

        return deleted;
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
            throw AccessDeniedException.FromAuthorization(decision, $"'{action}' denied ({decision.ReasonCode}).");
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

    private static readonly ResourceId LandscapeResource = new("atlas:landscape");

    // Encode the export scope + format into the audited resource id — metadata, not customer content (E4/E5).
    private static ResourceId ExportResource(string format) => new($"atlas:landscape/export?format={format}&scope=full");
}

/// <summary>
/// What the current principal may do in the catalogue — a small self-describing probe the UI uses to
/// decide whether to show author affordances. Not an atlas-contracts portability type: it describes the
/// live session, not held data.
/// </summary>
/// <param name="CanAuthor">Whether the principal may create, edit or delete catalogue entries.</param>
public sealed record CatalogueCapabilities(bool CanAuthor);
