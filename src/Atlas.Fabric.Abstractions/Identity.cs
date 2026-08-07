namespace Vev.Atlas.Fabric;

/// <summary>
/// The tenant an operation runs within. Isolation boundary for all data (fabric#3). Provider-neutral:
/// resolved from OIDC/tenant routing at the edge; a product never invents its own tenancy (15 §2).
/// </summary>
/// <param name="TenantId">Stable tenant identifier.</param>
public readonly record struct TenantContext(string TenantId)
{
    /// <summary>True when a real tenant is bound.</summary>
    public bool IsPresent => !string.IsNullOrWhiteSpace(TenantId);
}

/// <summary>
/// The authenticated subject performing an operation (fabric#3). Claims are provider-neutral;
/// Atlas reads identity from here, never from a raw token.
/// </summary>
/// <param name="PrincipalId">Stable subject identifier.</param>
/// <param name="DisplayName">Human-readable name for audit/UX.</param>
/// <param name="Roles">Coarse role names the principal holds in this tenant.</param>
public sealed record PrincipalContext(
    string PrincipalId,
    string? DisplayName,
    IReadOnlyCollection<string> Roles);

/// <summary>Ambient accessor for the current tenant + principal. Implemented by Fabric (dev shim for now).</summary>
public interface IRequestContextAccessor
{
    /// <summary>The tenant bound to the current request.</summary>
    TenantContext Tenant { get; }

    /// <summary>The principal bound to the current request.</summary>
    PrincipalContext Principal { get; }
}
