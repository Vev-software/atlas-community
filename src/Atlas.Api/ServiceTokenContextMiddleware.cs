using System.Security.Cryptography;
using System.Text;
using Vev.Atlas.Domain;
using Vev.Atlas.Fabric;
using Vev.Atlas.Fabric.Dev;

namespace Vev.Atlas.Api;

/// <summary>
/// Identity for a trusted machine-to-machine caller (the <c>service-token</c> mode). The caller proves it
/// is a legitimate service by presenting a configured shared secret in the
/// <c>X-Atlas-Service-Token</c> header; the tenant it acts on behalf of comes from <c>X-Tenant-Id</c>.
/// This is the identity source for a same-org backend forwarding reconciled landscape data into the
/// catalogue on a tenant's behalf, distinct from a human/browser caller (which uses Fabric OIDC).
/// <para>
/// Fail-closed by construction: a request without a matching token gets <c>401</c> and never reaches an
/// endpoint; a request with a valid token but no tenant gets <c>400</c>. The secret is compared in
/// constant time. Unlike the dev header shim, only the tenant is caller-supplied — the principal and its
/// role are fixed here, so a token holder cannot escalate its own roles, only name the tenant it writes.
/// </para>
/// </summary>
public sealed class ServiceTokenContextMiddleware(RequestDelegate next, byte[] expectedToken, string principalId, string[] roles)
{
    public const string TokenHeader = "X-Atlas-Service-Token";
    public const string TenantHeader = "X-Tenant-Id";

    public async Task InvokeAsync(HttpContext http)
    {
        var presented = http.Request.Headers[TokenHeader].ToString();
        if (string.IsNullOrEmpty(presented) || !TokenMatches(presented))
        {
            await FailAsync(http, StatusCodes.Status401Unauthorized, "missing_or_invalid_service_token");
            return;
        }

        var tenantId = http.Request.Headers[TenantHeader].ToString();
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            await FailAsync(http, StatusCodes.Status400BadRequest, "missing_tenant");
            return;
        }

        var tenant = new TenantContext(tenantId);
        var principal = new PrincipalContext(principalId, principalId, roles);

        using (AmbientRequestContextAccessor.BeginScope(tenant, principal, CorrelationId.For(http)))
        {
            await next(http);
        }
    }

    private bool TokenMatches(string presented) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), expectedToken);

    private static async Task FailAsync(HttpContext http, int statusCode, string reason)
    {
        http.Response.StatusCode = statusCode;
        await http.Response.WriteAsJsonAsync(new { error = "unauthenticated", reason });
    }
}
