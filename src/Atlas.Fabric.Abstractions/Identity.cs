namespace Vev.Atlas.Fabric;

/// <summary>Ambient accessor for the current tenant + principal. Implemented by Fabric (dev shim for now).</summary>
public interface IRequestContextAccessor
{
    /// <summary>The tenant bound to the current request.</summary>
    TenantContext Tenant { get; }

    /// <summary>The principal bound to the current request.</summary>
    PrincipalContext Principal { get; }
}
