namespace Vev.Atlas.Api;

/// <summary>
/// Rate-limit policy for creating a landscape share digest (atlas#175), same hardening as the export: a fixed window,
/// partitioned per tenant, configurable. Each digest also uses up a sequence number, so it must not be pullable in a loop.
/// </summary>
public static class ShareRateLimit
{
    /// <summary>Name of the named rate-limiter policy applied to <c>GET /api/v1/share/digest</c>.</summary>
    public const string PolicyName = "atlas-share";

    /// <summary>Config key for the number of digests allowed per window, per tenant.</summary>
    public const string PermitLimitKey = "Atlas:Share:PermitLimit";

    /// <summary>Config key for the fixed-window length, in seconds.</summary>
    public const string WindowSecondsKey = "Atlas:Share:WindowSeconds";

    public const int DefaultPermitLimit = 10;

    public const int DefaultWindowSeconds = 60;
}
