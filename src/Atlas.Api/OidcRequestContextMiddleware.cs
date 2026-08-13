using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Vev.Atlas.Fabric;
using Vev.Atlas.Fabric.Dev;

namespace Vev.Atlas.Api;

/// <summary>
/// Binds the tenant + principal for the current request from a <b>verified</b> OIDC bearer token onto the
/// ambient Fabric context (fabric#3). Unlike the development header shim
/// (<see cref="RequestContextMiddleware"/>), identity here comes from claims the authentication layer has
/// already validated against the provider, so a caller cannot assert its own tenant or roles.
/// <para>
/// This is the multi-tenant identity source: the tenant is read from the configured claim
/// (<see cref="OidcIdentityOptions.TenantClaim"/>), so one deployment serves many tenants, each isolated
/// by the tenant bound here (see the tenant query filter in <c>AtlasDbContext</c>).
/// </para>
/// </summary>
public sealed class OidcRequestContextMiddleware(RequestDelegate next, OidcIdentityOptions options)
{
    public async Task InvokeAsync(HttpContext http)
    {
        // Endpoints marked [AllowAnonymous] (health, and OpenAPI in dev) carry no identity — let them
        // through unbound so the container health probe works without a token.
        if (http.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            await next(http);
            return;
        }

        var user = http.User;

        // Fail closed: no verified principal → 401 (with a Bearer challenge). Atlas never runs a request
        // against reconnaissance-grade data without an authenticated identity (atlas#34).
        if (user.Identity?.IsAuthenticated != true)
        {
            await http.ChallengeAsync(JwtBearerDefaults.AuthenticationScheme);
            return;
        }

        // A verified token with no tenant claim cannot be scoped to a tenant, so it is refused rather than
        // run unscoped. This is a provider/token misconfiguration, not a credential problem.
        var tenantId = user.FindFirst(options.TenantClaim)?.Value;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var principalId = user.FindFirst(options.PrincipalClaim)?.Value
            ?? user.Identity.Name
            ?? tenantId;
        var displayName = user.FindFirst(options.NameClaim)?.Value ?? principalId;
        var roles = user.FindAll(options.RolesClaim).Select(claim => claim.Value).ToArray();

        var tenant = new TenantContext(tenantId);
        var principal = new PrincipalContext(principalId, displayName, roles);

        using (AmbientRequestContextAccessor.BeginScope(tenant, principal))
        {
            await next(http);
        }
    }
}
