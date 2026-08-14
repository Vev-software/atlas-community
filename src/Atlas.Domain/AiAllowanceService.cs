using Vev.Atlas.Fabric;

namespace Vev.Atlas.Domain;

/// <summary>
/// Resolves the current tenant's user-facing AI allowance from the Fabric entitlement limit plus the
/// append-only audit trail. This keeps the free/paid line entitlement-only while avoiding a separate
/// Community metering ledger.
/// </summary>
public sealed class AiAllowanceService(
    IRequestContextAccessor context,
    IEntitlementService entitlements,
    IAuditQueryService audit,
    TimeProvider clock)
{
    public AiAllowanceSnapshot Describe(CapabilityId capability, ResourceId resource)
    {
        var limit = entitlements.GetLimit(new EntitlementLimitRequest(
            context.Tenant,
            capability,
            context.Principal,
            resource));

        if (!limit.Available)
        {
            return new AiAllowanceSnapshot(
                capability.Value,
                AiAllowanceStatus.Unavailable,
                Allowed: false,
                Unlimited: false,
                Limit: null,
                Used: 0,
                Remaining: null,
                limit.Window,
                limit.ReasonCode,
                limit.Source);
        }

        if (limit.Unlimited)
        {
            return new AiAllowanceSnapshot(
                capability.Value,
                AiAllowanceStatus.Unlimited,
                Allowed: true,
                Unlimited: true,
                Limit: null,
                Used: 0,
                Remaining: null,
                limit.Window,
                ReasonCodes.Allow,
                limit.Source);
        }

        var window = UsageWindow(limit.Window);
        var used = audit.Query(context.Tenant.TenantId, capability.Value, window.FromInclusive, window.ToExclusive).Count;
        var remaining = Math.Max(0, limit.Limit!.Value - used);
        var allowed = remaining > 0;

        return new AiAllowanceSnapshot(
            capability.Value,
            allowed ? AiAllowanceStatus.Limited : AiAllowanceStatus.Exhausted,
            allowed,
            Unlimited: false,
            Limit: limit.Limit,
            Used: used,
            Remaining: remaining,
            limit.Window,
            allowed ? ReasonCodes.Allow : ReasonCodes.EntitlementLimitExhausted,
            limit.Source);
    }

    private (DateTimeOffset FromInclusive, DateTimeOffset ToExclusive) UsageWindow(string window)
    {
        var now = clock.GetUtcNow();
        return window switch
        {
            EntitlementLimitWindows.Day =>
                (new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero),
                 new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero)),
            _ => throw new CatalogueValidationException($"Unsupported entitlement window '{window}'.")
        };
    }
}

/// <summary>A resolved user-facing allowance snapshot for one AI capability.</summary>
public sealed record AiAllowanceSnapshot(
    string Capability,
    string Status,
    bool Allowed,
    bool Unlimited,
    int? Limit,
    int Used,
    int? Remaining,
    string Window,
    string ReasonCode,
    string Source);

/// <summary>Stable wire values for AI-allowance states.</summary>
public static class AiAllowanceStatus
{
    public const string Limited = "limited";
    public const string Unlimited = "unlimited";
    public const string Exhausted = "exhausted";
    public const string Unavailable = "unavailable";
}
