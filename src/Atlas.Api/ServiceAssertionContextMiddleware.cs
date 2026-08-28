using Vev.Atlas.Domain;
using Vev.Atlas.Fabric;
using Vev.Atlas.Fabric.Dev;
using Vev.Fabric.Contracts.Identity;

namespace Vev.Atlas.Api;

/// <summary>
/// Identity for a trusted machine caller authenticated by a Fabric service-identity assertion (the
/// <c>service-assertion</c> mode). The caller presents a short-lived, ECDSA-signed assertion in the
/// <c>X-Fabric-Service-Assertion</c> header; Atlas verifies it with only the caller's public key — no
/// shared secret — and acts on behalf of the single tenant the assertion names. This is the verified
/// machine identity that supersedes the interim <see cref="ServiceTokenContextMiddleware"/> shared token
/// for a same-org backend forwarding reconciled landscape data into the catalogue.
/// <para>
/// Fail-closed by construction: any invalid assertion (bad signature, unknown key, wrong issuer/audience,
/// expired, no tenant, ...) gets <c>401</c> with the specific reason code and never reaches an endpoint.
/// The caller names the tenant it writes (signed into the assertion, not a bare header), but its roles are
/// fixed by Atlas configuration here — so a caller cannot escalate its own privileges, only name the
/// tenant it acts for.
/// </para>
/// </summary>
public sealed class ServiceAssertionContextMiddleware(
    RequestDelegate next, ServiceAssertionValidator validator, string[] roles)
{
    public const string AssertionHeader = ServiceIdentity.AssertionHeaderName;

    public async Task InvokeAsync(HttpContext http)
    {
        var presented = http.Request.Headers[AssertionHeader].ToString();
        if (string.IsNullOrEmpty(presented))
        {
            await FailAsync(http, "missing_service_assertion");
            return;
        }

        var result = validator.Validate(presented);
        if (!result.IsValid)
        {
            await FailAsync(http, result.ReasonCode);
            return;
        }

        var assertion = result.Assertion!;
        var tenant = new TenantContext(assertion.TenantId);

        // Tenant + subject are cryptographically verified; roles are fixed by Atlas config so a caller
        // cannot assert its own privileges.
        var principal = new PrincipalContext(assertion.Subject, assertion.Subject, roles);

        using (AmbientRequestContextAccessor.BeginScope(tenant, principal, CorrelationId.For(http)))
        {
            await next(http);
        }
    }

    private static async Task FailAsync(HttpContext http, string reason)
    {
        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await http.Response.WriteAsJsonAsync(new { error = "unauthenticated", reason });
    }
}
