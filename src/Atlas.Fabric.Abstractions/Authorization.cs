namespace Vev.Atlas.Fabric;

/// <summary>
/// A capability identifier from the VEV-owned taxonomy (fabric#7): namespaced, stable, e.g.
/// <c>atlas.repository.application.max</c>. Products register their own <c>atlas.*</c> IDs;
/// adding IDs is fine, changing an existing ID's meaning is not.
/// </summary>
public readonly record struct CapabilityId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A resource an action targets, e.g. <c>atlas:asset/app-checkout</c>.</summary>
public readonly record struct ResourceId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>The outcome of an authorization or entitlement decision, with a machine-readable reason.</summary>
/// <param name="Allowed">Whether the action is permitted.</param>
/// <param name="ReasonCode">Stable reason code (e.g. <c>allow</c>, <c>role_missing</c>, <c>entitlement_denied</c>).</param>
/// <param name="Source">Where the decision came from (e.g. <c>authorizer</c>, <c>entitlement:snapshot</c>).</param>
public readonly record struct Decision(bool Allowed, string ReasonCode, string Source)
{
    /// <summary>An allow decision.</summary>
    public static Decision Allow(string source) => new(true, ReasonCodes.Allow, source);

    /// <summary>A deny decision with a reason code.</summary>
    public static Decision Deny(string reasonCode, string source) => new(false, reasonCode, source);
}

/// <summary>Stable reason codes shared by authorization and entitlement decisions (fabric#7).</summary>
public static class ReasonCodes
{
    /// <summary>The action is permitted.</summary>
    public const string Allow = "allow";

    /// <summary>The principal lacks a required role.</summary>
    public const string RoleMissing = "role_missing";

    /// <summary>The tenant has not been granted the capability.</summary>
    public const string EntitlementDenied = "entitlement_denied";

    /// <summary>The tenant's entitled allowance for the capability has been exhausted.</summary>
    public const string EntitlementLimitExhausted = "entitlement_limit_exhausted";

    /// <summary>No current entitlement snapshot; fail-static denies rather than grants (E6).</summary>
    public const string EntitlementUnavailable = "entitlement_unavailable";

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
