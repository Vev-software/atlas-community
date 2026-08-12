using Vev.Atlas.Fabric;
using Vev.Atlas.Fabric.Dev;

namespace Vev.Atlas.Api;

/// <summary>
/// Binds a <b>fixed</b> tenant + principal onto the ambient context for every request, taken from
/// configuration — never from request headers. This is the identity source for the single-tenant
/// self-hosted Community edition, where the whole catalogue is one tenant (atlas#34).
/// <para>
/// Unlike the development header shim (<see cref="RequestContextMiddleware"/>), the
/// <c>X-Tenant-Id</c> / <c>X-Principal-Id</c> / <c>X-Principal-Roles</c> headers are ignored here, so a
/// caller cannot name another tenant or escalate its own roles. Real multi-tenant identity is Fabric
/// OIDC (fabric#3).
/// </para>
/// </summary>
public sealed class SingleTenantContextMiddleware(RequestDelegate next, TenantContext tenant, PrincipalContext principal)
{
    public async Task InvokeAsync(HttpContext http)
    {
        using (AmbientRequestContextAccessor.BeginScope(tenant, principal))
        {
            await next(http);
        }
    }
}
