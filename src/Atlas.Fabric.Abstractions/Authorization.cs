namespace Vev.Atlas.Fabric;

/// <summary>The outcome of an authorization or entitlement decision, with a machine-readable reason.</summary>
/// <param name="Allowed">Whether the action is permitted.</param>
/// <param name="ReasonCode">Stable reason code (e.g. <c>allow</c>, <c>role_missing</c>, <c>entitlement_denied</c>).</param>
/// <param name="Source">Where the decision came from (e.g. <c>authorizer</c>, <c>entitlement:snapshot</c>).</param>
public readonly record struct Decision(bool Allowed, string ReasonCode, string Source)
{
    /// <summary>An allow decision.</summary>
    public static Decision Allow(string source) => new(true, Vev.Fabric.Contracts.Entitlements.ReasonCodes.Allow, source);

    /// <summary>A deny decision with a reason code.</summary>
    public static Decision Deny(string reasonCode, string source) => new(false, reasonCode, source);
}

/// <summary>Atlas-owned reason codes layered onto the shared Fabric contract.</summary>
public static class AtlasReasonCodes
{
    /// <summary>The tenant's entitled allowance for the capability has been exhausted.</summary>
    public const string EntitlementLimitExhausted = "entitlement_limit_exhausted";

    /// <summary>A module may not declare or satisfy a reserved paid capability (the open-core guard).</summary>
    public const string ReservedCapability = "reserved_capability";
}

/// <summary>
/// The authorization mechanism (fabric#5): "may principal P perform action A on resource R?".
/// Fabric owns the mechanism; a product supplies its role/permission definitions on top (11 §4).
/// </summary>
public interface IAuthorizer
{
    /// <summary>Decide whether the principal may perform the coarse action within the tenant.</summary>
    Decision Authorize(TenantContext tenant, PrincipalContext principal, string action, ResourceId resource);
}
