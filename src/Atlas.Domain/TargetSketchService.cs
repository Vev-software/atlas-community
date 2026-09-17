using Vev.Atlas.Contracts;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Domain;

public sealed record TargetIntent(string Entity, string Id, string Intent, string Rationale,
    Asset? Asset = null, Relationship? Relationship = null);
public sealed record TargetSketchRequest(string Name, IReadOnlyList<TargetIntent> Changes);
public sealed record TargetVersion(string Id, string Name, DateTimeOffset SavedAt,
    IReadOnlyList<Asset> Assets, IReadOnlyList<Relationship> Relationships, IReadOnlyList<TargetIntent> Changes);
public sealed record TargetSketchSnapshot(IReadOnlyList<TargetVersion> Versions, AiAllowanceSnapshot Allowance);

public interface ITargetVersionStore
{
    Task<IReadOnlyList<TargetVersion>> ListAsync(TenantContext tenant, CancellationToken ct);
    Task<int> CountAsync(TenantContext tenant, CancellationToken ct);
    Task<bool> TrySaveAsync(TenantContext tenant, TargetVersion version, int? limit, CancellationToken ct);
}

/// <summary>Entitlement allowance plus a durable saved-version counter, following AiAllowanceService.</summary>
public sealed class TargetAllowanceService(IRequestContextAccessor context,
    IEntitlementAllowanceProvider entitlements, ITargetVersionStore store)
{
    public async Task<AiAllowanceSnapshot> DescribeAsync(CancellationToken ct = default)
    {
        var limit = entitlements.Describe(new EntitlementAllowanceRequest(context.Tenant,
            AtlasCapabilities.TargetVersions, context.Principal, new ResourceId("atlas:target")));
        var used = await store.CountAsync(context.Tenant, ct);
        var available = limit.Available && (limit.Unlimited ||
            (limit.Window == EntitlementAllowanceWindows.Lifetime && limit.Limit > 0));
        var remaining = available && !limit.Unlimited ? Math.Max(0, limit.Limit!.Value - used) : (int?)null;
        var allowed = available && (limit.Unlimited || remaining > 0);
        return new AiAllowanceSnapshot(AtlasCapabilities.TargetVersions.Value,
            !available ? AiAllowanceStatus.Unavailable : limit.Unlimited ? AiAllowanceStatus.Unlimited :
            allowed ? AiAllowanceStatus.Limited : AiAllowanceStatus.Exhausted,
            allowed, available && limit.Unlimited, limit.Limit, used, remaining, limit.Window,
            allowed ? "allow" : available ? AtlasReasonCodes.EntitlementLimitExhausted : limit.Available ? "entitlement_unavailable" : limit.ReasonCode,
            limit.Source);
    }
}

/// <summary>Manual intent only: immutable target snapshots never mutate the held landscape.</summary>
public sealed class TargetSketchService(IRequestContextAccessor context, IAuthorizer authorizer,
    IAssetRepository assets, ITargetVersionStore store, TargetAllowanceService allowance,
    IAtlasAuditSink audit, TimeProvider clock)
{
    public async Task<TargetSketchSnapshot> GetAsync(CancellationToken ct)
    {
        Authorize(AtlasActions.AssetRead);
        return new(await store.ListAsync(context.Tenant, ct), await allowance.DescribeAsync(ct));
    }

    public async Task<TargetVersion> SaveAsync(TargetSketchRequest request, CancellationToken ct)
    {
        Authorize(AtlasActions.AssetWrite);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120 ||
            request.Changes is null || request.Changes.Count is 0 or > 500)
            throw new CatalogueValidationException("Provide a name (up to 120 characters) and 1–500 intents.");
        var heldAssets = (await assets.ListAssetsAsync(context.Tenant, null, ct)).ToList();
        var heldRelationships = (await assets.ListRelationshipsAsync(context.Tenant, ct)).ToList();
        var assetIds = heldAssets.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var relationshipIds = heldRelationships.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<(string, string)>();
        foreach (var change in request.Changes)
        {
            if (change is null || change.Entity is not ("asset" or "relationship") ||
                change.Intent is not ("planned-add" or "planned-change" or "planned-retire") ||
                string.IsNullOrWhiteSpace(change.Id) || change.Id.Length > 128 ||
                string.IsNullOrWhiteSpace(change.Rationale) || change.Rationale.Length > 500 ||
                !seen.Add((change.Entity, change.Id)))
                throw new CatalogueValidationException("Each intent needs a unique entity, ID, valid intent and rationale (up to 500 characters).");
            var ids = change.Entity == "asset" ? assetIds : relationshipIds;
            if (change.Intent != "planned-add")
            {
                if (!ids.Contains(change.Id) || change.Asset is not null || change.Relationship is not null)
                    throw new CatalogueValidationException("Change and retire intents must reference held facts only.");
                continue;
            }
            if (!ids.Add(change.Id)) throw new CatalogueValidationException("A planned addition must have a new ID.");
            if (change.Entity == "asset")
            {
                var asset = change.Asset;
                if (asset is null || asset.Id != change.Id || string.IsNullOrWhiteSpace(asset.Name) ||
                    asset.Name.Length > 256 || !Enum.IsDefined(asset.Kind) || !Enum.IsDefined(asset.Lifecycle) || change.Relationship is not null)
                    throw new CatalogueValidationException("A planned asset needs matching ID, name, kind and lifecycle.");
                heldAssets.Add(asset);
            }
            else
            {
                if (change.Relationship is not { } rel || rel.Id != change.Id || !Enum.IsDefined(rel.Type) || change.Asset is not null)
                    throw new CatalogueValidationException("A planned relationship needs matching ID and a valid type.");
                heldRelationships.Add(rel);
            }
        }
        if (heldRelationships.Any(r => !assetIds.Contains(r.FromId) || !assetIds.Contains(r.ToId)))
            throw new CatalogueValidationException("Relationship endpoints must exist in the held or planned assets.");
        var budget = await allowance.DescribeAsync(ct);
        if (!budget.Allowed) throw Denied(budget.ReasonCode, budget.Source);
        var version = new TargetVersion(Guid.NewGuid().ToString("N"), request.Name.Trim(), clock.GetUtcNow(),
            heldAssets, heldRelationships, request.Changes);
        if (!await store.TrySaveAsync(context.Tenant, version, budget.Unlimited ? null : budget.Limit, ct))
            throw Denied(AtlasReasonCodes.EntitlementLimitExhausted, budget.Source);
        await audit.WriteAsync(AtlasAudit.Event(context, clock, AtlasCapabilities.TargetVersions.Value,
            $"atlas:target/{version.Id}"), ct);
        return version;
    }

    private static AccessDeniedException Denied(string reason, string source) => new(new(reason, source),
        "Target-version allowance reached or unavailable. Review your entitlements to continue with Atlas Enterprise.");

    private void Authorize(string action)
    {
        var decision = authorizer.Authorize(context.Tenant, context.Principal, action, new ResourceId("atlas:target"));
        if (!decision.Allowed) throw AccessDeniedException.FromAuthorization(decision, "Target sketch access denied.");
    }
}
