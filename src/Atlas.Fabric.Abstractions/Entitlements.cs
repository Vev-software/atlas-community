namespace Vev.Atlas.Fabric;

/// <summary>
/// A request to evaluate whether a tenant may use a capability (fabric#4). This is distinct from
/// authorization: entitlement asks "has tenant T purchased capability C?", authz asks "may P do A?".
/// </summary>
/// <param name="Tenant">The tenant whose entitlement is evaluated.</param>
/// <param name="Capability">The capability from the VEV taxonomy.</param>
/// <param name="Principal">The principal in context (for audit/correlation).</param>
/// <param name="Resource">Optional resource the capability applies to.</param>
public readonly record struct EntitlementRequest(
    TenantContext Tenant,
    CapabilityId Capability,
    PrincipalContext Principal,
    ResourceId? Resource = null);

/// <summary>
/// A request to read the tenant's usage allowance for a capability. The entitlement snapshot may expose
/// a bounded free allowance (for example an <c>atlas.ai.*</c> daily budget) even when the capability is
/// not fully purchased, so products must read the limit rather than branching on a plan name.
/// </summary>
/// <param name="Tenant">The tenant whose entitlement limit is evaluated.</param>
/// <param name="Capability">The capability from the VEV taxonomy.</param>
/// <param name="Principal">The principal in context (for audit/correlation).</param>
/// <param name="Resource">Optional resource the capability applies to.</param>
public readonly record struct EntitlementLimitRequest(
    TenantContext Tenant,
    CapabilityId Capability,
    PrincipalContext Principal,
    ResourceId? Resource = null);

/// <summary>Stable entitlement-limit windows.</summary>
public static class EntitlementLimitWindows
{
    /// <summary>A daily allowance window, counted in UTC days.</summary>
    public const string Day = "day";

    /// <summary>No fixed allowance window because the capability is fully entitled.</summary>
    public const string None = "none";
}

/// <summary>
/// A capability-allowance snapshot from Fabric. A capability is either unavailable, bounded by a fixed
/// allowance, or fully entitled without a limit.
/// </summary>
/// <param name="Available">Whether the capability is available in some form.</param>
/// <param name="Unlimited">Whether the capability is fully entitled without a numeric allowance.</param>
/// <param name="Limit">The max uses in the current window, when bounded.</param>
/// <param name="Window">The fixed window label, e.g. <c>day</c>.</param>
/// <param name="ReasonCode">Stable reason code when unavailable.</param>
/// <param name="Source">Where the allowance snapshot came from.</param>
public readonly record struct EntitlementLimitSnapshot(
    bool Available,
    bool Unlimited,
    int? Limit,
    string Window,
    string ReasonCode,
    string Source)
{
    /// <summary>A deny snapshot.</summary>
    public static EntitlementLimitSnapshot Deny(string reasonCode, string source) =>
        new(false, false, null, EntitlementLimitWindows.None, reasonCode, source);

    /// <summary>An unlimited allowance snapshot.</summary>
    public static EntitlementLimitSnapshot UnlimitedAllowance(string source) =>
        new(true, true, null, EntitlementLimitWindows.None, ReasonCodes.Allow, source);

    /// <summary>A bounded fixed-window allowance snapshot.</summary>
    public static EntitlementLimitSnapshot FixedWindow(int limit, string window, string source)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Allowance limits must be positive.");
        }

        return new(true, false, limit, window, ReasonCodes.Allow, source);
    }
}

/// <summary>
/// The Fabric entitlement decision (fabric#4, handbook 09). The free/paid line of Atlas open-core
/// runs through this decision — never through <c>if (plan == "…")</c> (AGENTS.md §1.4). The local
/// evaluator reads a signed snapshot and is <b>fail-static</b>: an outage never silently grants (E6).
/// </summary>
public interface IEntitlementService
{
    /// <summary>Decide whether the tenant may use the requested capability, with a reason code.</summary>
    Decision Evaluate(EntitlementRequest request);

    /// <summary>Read the allowance snapshot for the requested capability.</summary>
    EntitlementLimitSnapshot GetLimit(EntitlementLimitRequest request);
}
