using Vev.Atlas.Domain;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Api;

/// <summary>
/// Chooses how the tenant + principal for a request are established, and <b>fails closed</b> when a
/// trustworthy source is not available (atlas#34).
/// <para>
/// Atlas holds reconnaissance-grade landscape data, so request identity must come from a verified
/// source, never from caller-supplied headers. The modes:
/// </para>
/// <list type="bullet">
///   <item><b>dev-headers</b> — the <c>X-Tenant-Id</c> / <c>X-Principal-Id</c> / <c>X-Principal-Roles</c>
///     header shim (<see cref="RequestContextMiddleware"/>). A development convenience, permitted
///     <b>only</b> in the Development environment.</item>
///   <item><b>single-tenant</b> — a fixed tenant + principal from configuration
///     (<see cref="SingleTenantContextMiddleware"/>), headers ignored. The identity source for the
///     single-tenant self-hosted Community edition.</item>
///   <item><b>fabric-oidc</b> — real identity from a verified Fabric OIDC token (fabric#3), not yet
///     wired. Selecting it (or defaulting to it outside Development) fails closed: the host refuses to
///     start rather than trust headers.</item>
/// </list>
/// <para>
/// When the mode is unset it defaults from the environment: Development → <b>dev-headers</b>; any other
/// environment → <b>fabric-oidc</b>, i.e. fail closed until a provider is configured. The self-hosted
/// container opts into <b>single-tenant</b> explicitly.
/// </para>
/// </summary>
public static class RequestIdentityConfiguration
{
    /// <summary>Optional explicit override of the identity mode; defaults from the hosting environment.</summary>
    public const string ModeKey = "Atlas:Identity:Mode";

    /// <summary>Fixed tenant id for <see cref="SingleTenant"/> mode. Defaults to <c>community</c>.</summary>
    public const string TenantKey = "Atlas:Identity:Tenant";

    /// <summary>Fixed principal id for <see cref="SingleTenant"/> mode. Defaults to <c>self-host</c>.</summary>
    public const string PrincipalKey = "Atlas:Identity:Principal";

    /// <summary>Comma-separated fixed roles for <see cref="SingleTenant"/> mode. Defaults to the Architect role.</summary>
    public const string RolesKey = "Atlas:Identity:Roles";

    /// <summary>Development-only header shim.</summary>
    public const string DevHeaders = "dev-headers";

    /// <summary>Fixed-identity single-tenant self-host.</summary>
    public const string SingleTenant = "single-tenant";

    /// <summary>Real identity resolved from a verified Fabric OIDC token (fabric#3).</summary>
    public const string FabricOidc = "fabric-oidc";

    /// <summary>
    /// Wire the request-identity source for the current environment, failing closed when no trustworthy
    /// provider is available. Call in place of registering the header middleware directly.
    /// </summary>
    public static WebApplication UseAtlasRequestIdentity(this WebApplication app)
    {
        var env = app.Environment;
        var configured = app.Configuration[ModeKey];
        var mode = string.IsNullOrWhiteSpace(configured)
            ? (env.IsDevelopment() ? DevHeaders : FabricOidc)
            : configured.Trim();

        switch (mode)
        {
            case DevHeaders:
                // The header shim trusts the caller, so it is only ever safe in local development.
                if (!env.IsDevelopment())
                {
                    throw new InvalidOperationException(
                        $"Refusing to start: the development header identity shim ('{ModeKey}={DevHeaders}') is only " +
                        $"permitted in the Development environment, not '{env.EnvironmentName}'. Use '{ModeKey}={SingleTenant}' " +
                        "for a self-hosted single-tenant deployment, or configure Fabric OIDC identity (fabric#3) (atlas#34).");
                }

                app.UseMiddleware<RequestContextMiddleware>();
                return app;

            case SingleTenant:
                // A fixed tenant + principal from configuration; request headers are ignored, so no caller
                // can name another tenant or escalate its roles. Safe in any environment.
                var tenant = new TenantContext(Value(app, TenantKey, "community"));
                var principalId = Value(app, PrincipalKey, "self-host");
                var roles = Value(app, RolesKey, AtlasRoles.Architect)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var principal = new PrincipalContext(principalId, principalId, roles);

                app.UseMiddleware<SingleTenantContextMiddleware>(tenant, principal);
                return app;

            case FabricOidc:
                // The real path resolves tenant/principal from a verified Fabric OIDC token (fabric#3).
                // That provider is not wired yet, so rather than fall back to header-asserted identity,
                // Atlas fails closed and refuses to start.
                throw new InvalidOperationException(
                    $"Refusing to start: no identity provider is configured for environment '{env.EnvironmentName}'. " +
                    $"Atlas does not fall back to header-asserted identity outside Development. Use '{ModeKey}={SingleTenant}' " +
                    "for a self-hosted single-tenant deployment, or configure Fabric OIDC identity (fabric#3) (atlas#34).");

            default:
                throw new InvalidOperationException(
                    $"Unknown '{ModeKey}' value '{mode}'. Expected '{DevHeaders}', '{SingleTenant}' or '{FabricOidc}'.");
        }
    }

    private static string Value(WebApplication app, string key, string fallback)
    {
        var configured = app.Configuration[key];
        return string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
    }
}
