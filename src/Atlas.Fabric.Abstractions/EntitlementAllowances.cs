namespace Vev.Atlas.Fabric;

/// <summary>
/// Atlas-specific allowance read over the Fabric entitlement snapshot. The public Fabric contract decides
/// whether a capability is granted; Atlas layers a small UX-focused allowance description on top so the
/// Community edition can show bounded AI hooks without inventing a plan model.
/// </summary>
public interface IEntitlementAllowanceProvider
{
    EntitlementAllowanceSnapshot Describe(EntitlementAllowanceRequest request);
}

/// <summary>
/// Request to describe a visible allowance for one capability.
/// </summary>
public readonly record struct EntitlementAllowanceRequest(
    TenantContext Tenant,
    CapabilityId Capability,
    PrincipalContext Principal,
    ResourceId? Resource = null);

/// <summary>Stable allowance windows used by the Atlas UX.</summary>
public static class EntitlementAllowanceWindows
{
    public const string Day = "day";
    public const string None = "none";
}

/// <summary>
/// User-facing allowance description for one capability.
/// </summary>
public readonly record struct EntitlementAllowanceSnapshot(
    bool Available,
    bool Unlimited,
    int? Limit,
    string Window,
    string ReasonCode,
    string Source)
{
    public static EntitlementAllowanceSnapshot Deny(string reasonCode, string source) =>
        new(false, false, null, EntitlementAllowanceWindows.None, reasonCode, source);

    public static EntitlementAllowanceSnapshot UnlimitedAllowance(string source) =>
        new(true, true, null, EntitlementAllowanceWindows.None, Vev.Fabric.Contracts.Entitlements.ReasonCodes.Allow, source);

    public static EntitlementAllowanceSnapshot FixedWindow(int limit, string window, string source)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Allowance limits must be positive.");
        }

        return new(true, false, limit, window, Vev.Fabric.Contracts.Entitlements.ReasonCodes.Allow, source);
    }
}
