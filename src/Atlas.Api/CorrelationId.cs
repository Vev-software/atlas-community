using System.Diagnostics;

namespace Vev.Atlas.Api;

/// <summary>
/// Resolves the correlation id for the current request, shared by every audit event emitted while
/// handling it (fabric#6). Prefers the ambient W3C trace id — which propagates to the substrate via
/// <c>traceparent</c>, so product and substrate events stitch together — and falls back to the
/// ASP.NET request identifier when no distributed trace is active.
/// </summary>
public static class CorrelationId
{
    /// <summary>The correlation id to bind for <paramref name="http"/>.</summary>
    public static string For(HttpContext http) =>
        Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier;
}
