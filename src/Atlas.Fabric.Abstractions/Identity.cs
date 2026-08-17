namespace Vev.Atlas.Fabric;

/// <summary>Ambient accessor for the current tenant + principal. Implemented by Fabric (dev shim for now).</summary>
public interface IRequestContextAccessor
{
    /// <summary>The tenant bound to the current request.</summary>
    TenantContext Tenant { get; }

    /// <summary>The principal bound to the current request.</summary>
    PrincipalContext Principal { get; }

    /// <summary>
    /// Correlation id shared by every event emitted while handling the current request, so product
    /// and substrate audit events stitch into one request story (fabric#6, 05 §2 Events).
    /// </summary>
    string CorrelationId { get; }
}
