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
/// The Fabric entitlement decision (fabric#4, handbook 09). The free/paid line of Atlas open-core
/// runs through this decision — never through <c>if (plan == "…")</c> (AGENTS.md §1.4). The local
/// evaluator reads a signed snapshot and is <b>fail-static</b>: an outage never silently grants (E6).
/// </summary>
public interface IEntitlementService
{
    /// <summary>Decide whether the tenant may use the requested capability, with a reason code.</summary>
    Decision Evaluate(EntitlementRequest request);
}
