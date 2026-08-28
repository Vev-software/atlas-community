using System.Linq;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Vev.Atlas.Domain;
using Vev.Atlas.Fabric;
using Vev.Fabric.Contracts.Identity;

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
///   <item><b>fabric-oidc</b> — real identity from a verified OIDC bearer token (fabric#3). The token is
///     validated against a configured provider (<see cref="OidcAuthorityKey"/>) and its claims are mapped
///     to the tenant + principal (<see cref="OidcRequestContextMiddleware"/>). Selecting it without a
///     provider configured fails closed: the host refuses to start rather than trust headers.</item>
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

    /// <summary>
    /// OIDC provider authority (issuer) for <see cref="FabricOidc"/> mode. Required to run that mode;
    /// when unset, the host fails closed rather than accept unverified identity.
    /// </summary>
    public const string OidcAuthorityKey = "Atlas:Identity:Oidc:Authority";

    /// <summary>Expected token audience (the client id) for <see cref="FabricOidc"/> mode. Optional.</summary>
    public const string OidcAudienceKey = "Atlas:Identity:Oidc:Audience";

    /// <summary>
    /// Browser-facing OIDC authority for interactive sign-in. Optional; when unset, Atlas falls back to the
    /// token-validation authority. Set this when the API sees an internal issuer URL but browsers must use a
    /// different public origin (for example Docker Compose with Keycloak on localhost:8081).
    /// </summary>
    public const string OidcBrowserAuthorityKey = "Atlas:Identity:Oidc:BrowserAuthority";

    /// <summary>The OIDC client id the browser login page uses for direct grant. Defaults to <c>atlas-api</c>.</summary>
    public const string OidcClientIdKey = "Atlas:Identity:Oidc:ClientId";

    /// <summary>Token claim carrying the tenant id. Defaults to <c>tenant</c>.</summary>
    public const string OidcTenantClaimKey = "Atlas:Identity:Oidc:TenantClaim";

    /// <summary>Token claim carrying the stable principal id. Defaults to the OIDC <c>sub</c> claim.</summary>
    public const string OidcPrincipalClaimKey = "Atlas:Identity:Oidc:PrincipalClaim";

    /// <summary>Token claim carrying the human-readable name. Defaults to <c>name</c>.</summary>
    public const string OidcNameClaimKey = "Atlas:Identity:Oidc:NameClaim";

    /// <summary>Token claim carrying role names (may repeat). Defaults to <c>roles</c>.</summary>
    public const string OidcRolesClaimKey = "Atlas:Identity:Oidc:RolesClaim";

    /// <summary>
    /// Whether provider metadata must be fetched over HTTPS. Defaults to <c>true</c>; a local dev
    /// Keycloak over plain HTTP sets it to <c>false</c>.
    /// </summary>
    public const string OidcRequireHttpsMetadataKey = "Atlas:Identity:Oidc:RequireHttpsMetadata";

    /// <summary>Development-only header shim.</summary>
    public const string DevHeaders = "dev-headers";

    /// <summary>Fixed-identity single-tenant self-host.</summary>
    public const string SingleTenant = "single-tenant";

    /// <summary>Real identity resolved from a verified Fabric OIDC token (fabric#3).</summary>
    public const string FabricOidc = "fabric-oidc";

    /// <summary>
    /// Trusted machine-to-machine caller authenticated by a configured shared secret
    /// (<see cref="ServiceTokenSecretKey"/>), acting on behalf of the tenant it names in
    /// <c>X-Tenant-Id</c>. For a same-org backend forwarding reconciled landscape data into the
    /// catalogue; see <see cref="ServiceTokenContextMiddleware"/>. An explicit opt-in — never a default.
    /// </summary>
    public const string ServiceToken = "service-token";

    /// <summary>Shared secret for <see cref="ServiceToken"/> mode. Required to run it; unset fails closed.</summary>
    public const string ServiceTokenSecretKey = "Atlas:Identity:ServiceToken:Secret";

    /// <summary>Fixed principal id recorded for <see cref="ServiceToken"/> callers. Defaults to <c>service</c>.</summary>
    public const string ServiceTokenPrincipalKey = "Atlas:Identity:ServiceToken:Principal";

    /// <summary>Comma-separated fixed roles for <see cref="ServiceToken"/> callers. Defaults to the Architect role.</summary>
    public const string ServiceTokenRolesKey = "Atlas:Identity:ServiceToken:Roles";

    /// <summary>
    /// Trusted machine-to-machine caller authenticated by a Fabric service-identity assertion — a
    /// short-lived, ECDSA-signed token presented in <c>X-Fabric-Service-Assertion</c> and verified with
    /// only the caller's public key (no shared secret). Supersedes <see cref="ServiceToken"/> for a same-org
    /// backend forwarding reconciled landscape data; see <see cref="ServiceAssertionContextMiddleware"/>. An
    /// explicit opt-in — never a default.
    /// </summary>
    public const string ServiceAssertion = "service-assertion";

    /// <summary>Caller's PEM-encoded EC public key for <see cref="ServiceAssertion"/> mode. Required; unset fails closed.</summary>
    public const string ServiceAssertionPublicKeyKey = "Atlas:Identity:ServiceAssertion:PublicKeyPem";

    /// <summary>Key id (<c>kid</c>) the assertion's signing key is looked up by. Required for <see cref="ServiceAssertion"/> mode.</summary>
    public const string ServiceAssertionKeyIdKey = "Atlas:Identity:ServiceAssertion:KeyId";

    /// <summary>Expected assertion issuer (<c>iss</c>). Required for <see cref="ServiceAssertion"/> mode.</summary>
    public const string ServiceAssertionIssuerKey = "Atlas:Identity:ServiceAssertion:Issuer";

    /// <summary>Expected assertion audience (<c>aud</c>) — this Atlas service's id. Required for <see cref="ServiceAssertion"/> mode.</summary>
    public const string ServiceAssertionAudienceKey = "Atlas:Identity:ServiceAssertion:Audience";

    /// <summary>Comma-separated fixed roles for <see cref="ServiceAssertion"/> callers. Defaults to the Architect role.</summary>
    public const string ServiceAssertionRolesKey = "Atlas:Identity:ServiceAssertion:Roles";

    /// <summary>
    /// Register the authentication services the resolved identity mode needs, before the host is built.
    /// For <see cref="FabricOidc"/> with a configured <see cref="OidcAuthorityKey"/> this wires JWT bearer
    /// validation against the provider; the other modes need no service registration here. Pair with
    /// <see cref="UseAtlasRequestIdentity"/>, which wires the matching request pipeline.
    /// </summary>
    public static WebApplicationBuilder AddAtlasRequestIdentity(this WebApplicationBuilder builder)
    {
        var mode = ResolveMode(builder.Environment, builder.Configuration);
        if (mode != FabricOidc)
        {
            return builder;
        }

        var authority = builder.Configuration[OidcAuthorityKey];
        if (string.IsNullOrWhiteSpace(authority))
        {
            // No provider yet: UseAtlasRequestIdentity will fail closed. Nothing to register.
            return builder;
        }

        var options = OidcIdentityOptions.FromConfiguration(builder.Configuration);
        var audience = builder.Configuration[OidcAudienceKey];
        var requireHttpsMetadata = builder.Configuration.GetValue(OidcRequireHttpsMetadataKey, true);

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.Authority = authority;
                jwt.RequireHttpsMetadata = requireHttpsMetadata;

                // Keep JWT claim types verbatim (sub, tenant, roles, name) rather than remapping them to
                // the legacy long ClaimTypes.* URIs, so the middleware reads exactly the configured claims.
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters.NameClaimType = options.NameClaim;
                jwt.TokenValidationParameters.RoleClaimType = options.RolesClaim;

                if (string.IsNullOrWhiteSpace(audience))
                {
                    jwt.TokenValidationParameters.ValidateAudience = false;
                }
                else
                {
                    jwt.Audience = audience;
                }
            });
        builder.Services.AddAuthorization();

        return builder;
    }

    /// <summary>
    /// Wire the request-identity source for the current environment, failing closed when no trustworthy
    /// provider is available. Call in place of registering the header middleware directly.
    /// </summary>
    public static WebApplication UseAtlasRequestIdentity(this WebApplication app)
    {
        var env = app.Environment;
        var mode = ResolveMode(env, app.Configuration);

        switch (mode)
        {
            case DevHeaders:
                // The header shim trusts the caller, so it is only ever safe in local development.
                if (!env.IsDevelopment())
                {
                    throw new InvalidOperationException(
                        $"Refusing to start: the development header identity shim ('{ModeKey}={DevHeaders}') is only " +
                        $"permitted in the Development environment, not '{env.EnvironmentName}'. Use '{ModeKey}={SingleTenant}' " +
                        $"for a self-hosted single-tenant deployment, or configure Fabric OIDC identity ('{OidcAuthorityKey}', fabric#3) (atlas#34).");
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
                // The real path resolves tenant/principal from a verified OIDC token (fabric#3). Without a
                // configured provider Atlas fails closed rather than fall back to header-asserted identity.
                var authority = app.Configuration[OidcAuthorityKey];
                if (string.IsNullOrWhiteSpace(authority))
                {
                    throw new InvalidOperationException(
                        $"Refusing to start: no identity provider is configured for environment '{env.EnvironmentName}'. " +
                        $"Atlas does not fall back to header-asserted identity outside Development. Set '{OidcAuthorityKey}' to a " +
                        $"verified OIDC provider for multi-tenant Fabric identity (fabric#3), or use '{ModeKey}={SingleTenant}' " +
                        "for a self-hosted single-tenant deployment (atlas#34).");
                }

                // Authentication validates the bearer token; the middleware maps its claims onto the ambient
                // tenant + principal and fails closed on an unauthenticated or tenant-less request.
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseMiddleware<OidcRequestContextMiddleware>(OidcIdentityOptions.FromConfiguration(app.Configuration));
                return app;

            case ServiceToken:
                // A trusted machine caller proves itself with a configured shared secret; without one
                // Atlas fails closed rather than accept an unauthenticated service call.
                var secret = app.Configuration[ServiceTokenSecretKey];
                if (string.IsNullOrWhiteSpace(secret))
                {
                    throw new InvalidOperationException(
                        $"Refusing to start: '{ModeKey}={ServiceToken}' requires a shared secret at '{ServiceTokenSecretKey}'. " +
                        "Atlas does not accept unauthenticated service calls.");
                }

                var servicePrincipal = Value(app, ServiceTokenPrincipalKey, "service");
                var serviceRoles = Value(app, ServiceTokenRolesKey, AtlasRoles.Architect)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                app.UseMiddleware<ServiceTokenContextMiddleware>(
                    System.Text.Encoding.UTF8.GetBytes(secret), servicePrincipal, serviceRoles);
                return app;

            case ServiceAssertion:
                // A trusted machine caller proves itself with a signed assertion Atlas verifies using only
                // the caller's public key. Without the verifying key/issuer/audience configured, Atlas fails
                // closed rather than accept an unverifiable service call.
                var publicKeyPem = app.Configuration[ServiceAssertionPublicKeyKey];
                var keyId = app.Configuration[ServiceAssertionKeyIdKey];
                var expectedIssuer = app.Configuration[ServiceAssertionIssuerKey];
                var expectedAudience = app.Configuration[ServiceAssertionAudienceKey];
                if (string.IsNullOrWhiteSpace(publicKeyPem) || string.IsNullOrWhiteSpace(keyId)
                    || string.IsNullOrWhiteSpace(expectedIssuer) || string.IsNullOrWhiteSpace(expectedAudience))
                {
                    throw new InvalidOperationException(
                        $"Refusing to start: '{ModeKey}={ServiceAssertion}' requires the caller's public key, key id, " +
                        $"issuer and audience at '{ServiceAssertionPublicKeyKey}', '{ServiceAssertionKeyIdKey}', " +
                        $"'{ServiceAssertionIssuerKey}' and '{ServiceAssertionAudienceKey}'. Atlas does not accept " +
                        "unverifiable service calls.");
                }

                var assertionValidator = ServiceAssertionValidator.FromPem(
                    keyId, publicKeyPem, expectedIssuer, expectedAudience);
                var assertionRoles = Value(app, ServiceAssertionRolesKey, AtlasRoles.Architect)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                app.UseMiddleware<ServiceAssertionContextMiddleware>(assertionValidator, assertionRoles);
                return app;

            default:
                throw new InvalidOperationException(
                    $"Unknown '{ModeKey}' value '{mode}'. Expected '{DevHeaders}', '{SingleTenant}', '{FabricOidc}', " +
                    $"'{ServiceToken}' or '{ServiceAssertion}'.");
        }
    }

    /// <summary>
    /// Resolve the effective identity mode: an explicit <see cref="ModeKey"/> override, otherwise the
    /// environment default (Development → dev-headers; anything else → fabric-oidc, i.e. fail closed).
    /// </summary>
    private static string ResolveMode(IHostEnvironment env, IConfiguration config)
    {
        var configured = config[ModeKey];
        return string.IsNullOrWhiteSpace(configured)
            ? (env.IsDevelopment() ? DevHeaders : FabricOidc)
            : configured.Trim();
    }

    private static string Value(WebApplication app, string key, string fallback)
    {
        var configured = app.Configuration[key];
        return string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
    }
}

/// <summary>
/// The token claims <see cref="OidcRequestContextMiddleware"/> reads to build the tenant + principal.
/// Claim names are configurable so Atlas stays provider-neutral (handbook 05 §6): the operator points
/// each field at whatever claim their OIDC provider emits.
/// </summary>
/// <param name="TenantClaim">Claim carrying the tenant id (default <c>tenant</c>).</param>
/// <param name="PrincipalClaim">Claim carrying the stable principal id (default <c>sub</c>).</param>
/// <param name="NameClaim">Claim carrying the display name (default <c>name</c>).</param>
/// <param name="RolesClaim">Claim carrying role names, possibly repeated (default <c>roles</c>).</param>
public sealed record OidcIdentityOptions(
    string TenantClaim,
    string PrincipalClaim,
    string NameClaim,
    string RolesClaim)
{
    /// <summary>Read the claim names from configuration, applying the OIDC-conventional defaults.</summary>
    public static OidcIdentityOptions FromConfiguration(IConfiguration config) => new(
        Value(config, RequestIdentityConfiguration.OidcTenantClaimKey, "tenant"),
        Value(config, RequestIdentityConfiguration.OidcPrincipalClaimKey, "sub"),
        Value(config, RequestIdentityConfiguration.OidcNameClaimKey, "name"),
        Value(config, RequestIdentityConfiguration.OidcRolesClaimKey, "roles"));

    private static string Value(IConfiguration config, string key, string fallback)
    {
        var configured = config[key];
        return string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
    }
}

/// <summary>
/// Browser-facing OIDC settings for the static login page. These may differ from the API's token-validation
/// authority when the server sees an internal issuer URL but users sign in through a public endpoint.
/// </summary>
/// <param name="Authority">Public authority/realm base the browser talks to, e.g. <c>https://id.example.com/realms/atlas</c>.</param>
/// <param name="ClientId">Client id used for direct-grant token requests.</param>
public sealed record OidcBrowserOptions(
    string Authority,
    string ClientId)
{
    /// <summary>Keycloak account console URL under the configured authority.</summary>
    public string AccountUrl => Authority + "/account";

    /// <summary>
    /// Resolve the browser-facing authority + client id. Returns <c>null</c> when no OIDC provider is configured.
    /// Falls back from <see cref="RequestIdentityConfiguration.OidcBrowserAuthorityKey"/> to
    /// <see cref="RequestIdentityConfiguration.OidcAuthorityKey"/>.
    /// </summary>
    public static OidcBrowserOptions? FromConfiguration(IConfiguration config)
    {
        var authority = FirstNonBlank(
            config[RequestIdentityConfiguration.OidcBrowserAuthorityKey],
            config[RequestIdentityConfiguration.OidcAuthorityKey]);
        if (authority is null)
        {
            return null;
        }

        return new OidcBrowserOptions(
            NormalizeUrl(authority),
            Value(config, RequestIdentityConfiguration.OidcClientIdKey, "atlas-api"));
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string NormalizeUrl(string url) => url.Trim().TrimEnd('/');

    private static string Value(IConfiguration config, string key, string fallback)
    {
        var configured = config[key];
        return string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
    }
}
