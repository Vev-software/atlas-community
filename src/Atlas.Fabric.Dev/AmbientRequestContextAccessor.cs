using Vev.Atlas.Fabric;

namespace Vev.Atlas.Fabric.Dev;

/// <summary>
/// Dev implementation of <see cref="IRequestContextAccessor"/>: the tenant + principal for the current
/// logical flow are held in an <see cref="AsyncLocal{T}"/>, set per request by the hosting layer.
/// When real Fabric identity lands, this is replaced by the Fabric-provided accessor.
/// </summary>
public sealed class AmbientRequestContextAccessor : IRequestContextAccessor
{
    private static readonly AsyncLocal<Scope?> Current = new();

    /// <inheritdoc />
    public TenantContext Tenant => Current.Value?.Tenant
        ?? throw new InvalidOperationException("No request context bound. Call BeginScope first.");

    /// <inheritdoc />
    public PrincipalContext Principal => Current.Value?.Principal
        ?? throw new InvalidOperationException("No request context bound. Call BeginScope first.");

    /// <summary>Bind a tenant + principal for the current flow; dispose to clear.</summary>
    public static IDisposable BeginScope(TenantContext tenant, PrincipalContext principal)
    {
        var previous = Current.Value;
        Current.Value = new Scope(tenant, principal);
        return new Restore(previous);
    }

    private sealed record Scope(TenantContext Tenant, PrincipalContext Principal);

    private sealed class Restore(Scope? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
