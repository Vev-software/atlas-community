using Vev.Atlas.Fabric;

namespace Vev.Atlas.Fabric.Dev;

/// <summary>
/// Dev entitlement evaluator standing in for the Fabric entitlement service (fabric#4). It reads a
/// static set of granted capabilities — the local, offline "snapshot" — and is <b>fail-static</b>:
/// a capability that is not explicitly granted is denied, never silently allowed (handbook 09 §4, E6).
/// <para>
/// In the Community Edition the granted set is <b>empty</b>, so every paid capability is denied while
/// the free asset-management capabilities (which do not pass through entitlement) run fully.
/// </para>
/// </summary>
public sealed class CommunityEntitlementService(IReadOnlySet<string> grantedCapabilities) : IEntitlementService
{
    private const string Source = "entitlement:local-snapshot";

    /// <summary>An evaluator with no granted paid capabilities — the Community Edition default.</summary>
    public static CommunityEntitlementService Community { get; } =
        new(new HashSet<string>(StringComparer.Ordinal));

    /// <inheritdoc />
    public Decision Evaluate(EntitlementRequest request)
    {
        // Fail-static: only an explicit grant allows. No grant, or an unknown capability, denies.
        return grantedCapabilities.Contains(request.Capability.Value)
            ? Decision.Allow(Source)
            : Decision.Deny(ReasonCodes.EntitlementDenied, Source);
    }
}
