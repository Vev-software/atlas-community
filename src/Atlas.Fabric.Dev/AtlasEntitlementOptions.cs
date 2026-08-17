namespace Vev.Atlas.Fabric.Dev;

/// <summary>
/// Configuration for the local Fabric entitlement evaluator Atlas runs on the request path.
/// </summary>
public sealed class AtlasEntitlementOptions
{
    public const string SectionName = "Atlas:Entitlements";

    /// <summary>Inline signed snapshot document JSON to import at startup.</summary>
    public string? SnapshotDocumentJson { get; set; }

    /// <summary>Path to a signed snapshot document to import at startup (for air-gapped/self-hosted installs).</summary>
    public string? SnapshotDocumentPath { get; set; }

    /// <summary>Remote endpoint returning a signed snapshot document; refreshed periodically when configured.</summary>
    public string? SnapshotDocumentUrl { get; set; }

    /// <summary>How often the connected snapshot source is refreshed.</summary>
    public int SnapshotRefreshSeconds { get; set; } = 300;

    /// <summary>
    /// Trust anchors keyed by snapshot key id. Values are base64-encoded symmetric keys for the current
    /// reference HMAC verifier from <c>Vev.Fabric.Contracts</c>.
    /// </summary>
    public Dictionary<string, string> TrustedKeys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Community-visible daily AI structuring allowance when no signed entitlement snapshot is configured.
    /// Preserves the current Community UX while paid entitlement evaluation moves to the Fabric contract.
    /// </summary>
    public int CommunityAiStructureDailyLimit { get; set; } = 3;
}
