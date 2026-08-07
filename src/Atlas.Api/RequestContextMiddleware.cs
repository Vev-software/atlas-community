using Vev.Atlas.Domain;
using Vev.Atlas.Fabric;
using Vev.Atlas.Fabric.Dev;

namespace Vev.Atlas.Api;

/// <summary>
/// Binds the tenant + principal for the current request onto the ambient Fabric context. In this dev
/// shim they come from <c>X-Tenant-Id</c> / <c>X-Principal-Id</c> / <c>X-Principal-Roles</c> headers,
/// defaulting to a single dev tenant. When real Fabric identity lands, this is replaced by the
/// Fabric-provided authentication that resolves the same context from OIDC (fabric#3).
/// </summary>
public sealed class RequestContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        var tenantId = Header(http, "X-Tenant-Id") ?? "dev";
        var principalId = Header(http, "X-Principal-Id") ?? "dev-user";
        var roles = (Header(http, "X-Principal-Roles") ?? AtlasRoles.Architect)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var tenant = new TenantContext(tenantId);
        var principal = new PrincipalContext(principalId, principalId, roles);

        using (AmbientRequestContextAccessor.BeginScope(tenant, principal))
        {
            await next(http);
        }
    }

    private static string? Header(HttpContext http, string name) =>
        http.Request.Headers.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v.ToString()
            : null;
}
