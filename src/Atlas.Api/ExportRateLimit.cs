namespace Vev.Atlas.Api;

/// <summary>
/// Rate-limit policy for the whole-landscape export (atlas#36). A full-map export is the highest-value
/// reconnaissance read, so it must not be pullable in a tight loop. The limit is a fixed window,
/// partitioned per tenant, and is configurable so an operator can tune it (and tests can drive it).
/// </summary>
public static class ExportRateLimit
{
    /// <summary>Name of the named rate-limiter policy applied to <c>GET /api/v1/export</c>.</summary>
    public const string PolicyName = "atlas-export";

    /// <summary>Config key for the number of exports allowed per window, per tenant.</summary>
    public const string PermitLimitKey = "Atlas:Export:PermitLimit";

    /// <summary>Config key for the fixed-window length, in seconds.</summary>
    public const string WindowSecondsKey = "Atlas:Export:WindowSeconds";

    /// <summary>Default exports allowed per window, per tenant.</summary>
    public const int DefaultPermitLimit = 10;

    /// <summary>Default fixed-window length, in seconds.</summary>
    public const int DefaultWindowSeconds = 60;
}
