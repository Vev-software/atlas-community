using Vev.Atlas.Fabric;
using Vev.Fabric.Contracts.Entitlements;

namespace Vev.Atlas.Domain;

/// <summary>
/// Composes a single, read-only "my licence &amp; entitlements" view for the current tenant (atlas#147).
/// It is a pure reflection of the server-side entitlement decision: it evaluates each user-facing paid
/// capability through the same <see cref="IEntitlementService"/> the gates use, and reports what the tenant
/// holds, what is denied, and the licence window — it never takes an entitlement decision of its own.
/// </summary>
public sealed class EntitlementSummaryService(
    IRequestContextAccessor context,
    IEntitlementService entitlements,
    AiAllowanceService aiAllowances,
    TimeProvider clock)
{
    private const string CommunitySourcePrefix = "entitlement:community";
    private static readonly ResourceId TenantResource = new("atlas:tenant");
    private static readonly ResourceId StructureResource = new("atlas:structure-draft");

    // How close to expiry the licence is called "expiring" so the panel can nudge a renewal.
    private static readonly TimeSpan ExpiryWarningWindow = TimeSpan.FromDays(14);

    /// <summary>
    /// Reason codes that mean a licence is <em>present but not granting</em> (stale, tampered, wrong tenant,
    /// clock regression, or a lifecycle hold). These deny closed but are not a plain Community not-granted, so
    /// the panel surfaces them as "needs attention" rather than "you are on Community".
    /// </summary>
    private static readonly IReadOnlySet<string> LicenceAttentionReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        ReasonCodes.EntitlementUnavailable,
        ReasonCodes.EntitlementSnapshotInvalid,
        ReasonCodes.EntitlementSnapshotStale,
        ReasonCodes.EntitlementSnapshotTenantMismatch,
        ReasonCodes.EntitlementSnapshotRolledBack,
        ReasonCodes.EntitlementClockRegression,
        ReasonCodes.LifecycleTrialExpired,
        ReasonCodes.LifecycleReadOnly,
        ReasonCodes.LifecycleLocked,
        ReasonCodes.LifecycleRetention,
        ReasonCodes.LifecyclePurged,
        ReasonCodes.TrialExpired,
    };

    /// <summary>
    /// The user-facing paid capabilities, grouped for display. The legacy <c>atlas.portfolio.apm</c> id is
    /// deliberately omitted in favour of the canonical <see cref="AtlasCapabilities.PortfolioAnalysis"/>
    /// (<c>atlas.analysis.apm</c>) pending vocabulary reconciliation, so the panel shows one portfolio row,
    /// not two. Everything here is a reserved paid capability — Community denies each with a reason code.
    /// </summary>
    private static readonly IReadOnlyList<CapabilityDescriptor> DisplayCapabilities = new[]
    {
        new CapabilityDescriptor(AtlasCapabilities.IntegrationMapping, "Integration mapping", "Core"),
        new CapabilityDescriptor(AtlasCapabilities.EndOfLifeTracking, "End-of-life tracking", "Core"),
        new CapabilityDescriptor(AtlasCapabilities.PortfolioAnalysis, "Portfolio analysis (APM)", "Core"),
        new CapabilityDescriptor(AtlasCapabilities.RoadmapGeneration, "Roadmap generation", "AI"),
        new CapabilityDescriptor(AtlasCapabilities.AiReview, "AI architecture review", "AI"),
        new CapabilityDescriptor(AtlasCapabilities.AiGenerate, "AI deliverable generation", "AI"),
        new CapabilityDescriptor(AtlasCapabilities.DataIntrospection, "Data introspection", "Data"),
        new CapabilityDescriptor(AtlasCapabilities.DataOverlap, "Data overlap analysis", "Data"),
        new CapabilityDescriptor(AtlasCapabilities.DataQuality, "Data quality", "Data"),
        new CapabilityDescriptor(AtlasCapabilities.ArchimateExport, "ArchiMate export", "Interoperability"),
    };

    /// <summary>Describe the current tenant's edition, licence status, entitlements and free AI allowance.</summary>
    public EntitlementSummary Describe()
    {
        var principal = context.Principal;
        var tenant = context.Tenant;

        var capabilities = new List<CapabilitySummary>(DisplayCapabilities.Count);
        foreach (var descriptor in DisplayCapabilities)
        {
            var decision = entitlements.Evaluate(new EntitlementRequest(tenant, descriptor.Capability, principal, TenantResource));
            capabilities.Add(new CapabilitySummary(
                descriptor.Capability.Value,
                descriptor.Label,
                descriptor.Category,
                decision.Allowed,
                decision.ReasonCode,
                decision.Source,
                decision.ValidUntil));
        }

        var identity = new PrincipalSummary(
            principal.PrincipalId,
            principal.DisplayName,
            principal.Roles.ToArray(),
            tenant.TenantId);

        var licence = BuildLicenceStatus(capabilities);

        // The free landscape-structuring hook is Community's own adoption affordance, not a licensed
        // capability, so it is reported alongside the licence as the one allowance a Community tenant always
        // has. The service reuses the same allowance evaluation the AI endpoints do.
        var aiStructure = aiAllowances.Describe(AtlasCapabilities.AiStructure, StructureResource);

        return new EntitlementSummary(licence.Edition, licence, identity, capabilities, aiStructure);
    }

    private LicenceStatus BuildLicenceStatus(IReadOnlyList<CapabilitySummary> capabilities)
    {
        var now = clock.GetUtcNow();

        // Prefer the source of a granted capability (that is the live licence); otherwise the first denial's
        // source still tells us whether a snapshot is configured at all.
        var source = capabilities.FirstOrDefault(c => c.Enabled)?.Source
            ?? capabilities.FirstOrDefault()?.Source
            ?? "entitlement:community-default";

        var isLicensed = !source.StartsWith(CommunitySourcePrefix, StringComparison.Ordinal);
        var attention = capabilities.Any(c => !c.Enabled && LicenceAttentionReasons.Contains(c.ReasonCode));
        var granted = capabilities.Where(c => c.Enabled).ToArray();

        // The licence window is the earliest expiry among the granted capabilities (a snapshot typically
        // shares one expiry, but taking the minimum is honest when grants differ).
        DateTimeOffset? validUntil = granted
            .Select(c => c.ValidUntil)
            .Where(v => v is not null)
            .DefaultIfEmpty(null)
            .Min();

        if (!isLicensed && !attention)
        {
            return new LicenceStatus(
                Edition: "Community",
                State: "community",
                Source: source,
                ValidUntil: null,
                Summary: "Atlas Community edition. Free capabilities are available; the paid capabilities below are reserved for a licensed edition.");
        }

        if (attention)
        {
            return new LicenceStatus(
                Edition: "Licensed",
                State: "attention",
                Source: source,
                ValidUntil: validUntil,
                Summary: "A licence is configured but is not currently granting capabilities. Check the licence snapshot (it may be expired, stale or issued for another tenant).");
        }

        if (validUntil is { } expiry && expiry - now <= ExpiryWarningWindow)
        {
            return new LicenceStatus(
                Edition: "Licensed",
                State: "expiring",
                Source: source,
                ValidUntil: validUntil,
                Summary: $"Licensed edition. The licence expires on {expiry.UtcDateTime:yyyy-MM-dd} — renew to keep the paid capabilities enabled.");
        }

        return new LicenceStatus(
            Edition: "Licensed",
            State: "active",
            Source: source,
            ValidUntil: validUntil,
            Summary: "Licensed edition. The paid capabilities enabled below are covered by an active licence.");
    }

    private sealed record CapabilityDescriptor(CapabilityId Capability, string Label, string Category);
}

/// <summary>The composed licence &amp; entitlements view for the current tenant (atlas#147).</summary>
public sealed record EntitlementSummary(
    string Edition,
    LicenceStatus Licence,
    PrincipalSummary Identity,
    IReadOnlyList<CapabilitySummary> Capabilities,
    AiAllowanceSnapshot AiStructure);

/// <summary>Who the current request is acting as, mirrored from the request context.</summary>
public sealed record PrincipalSummary(
    string PrincipalId,
    string? DisplayName,
    IReadOnlyList<string> Roles,
    string Tenant);

/// <summary>The tenant's edition and licence health, derived from the entitlement decisions.</summary>
/// <param name="Edition">"Community" or "Licensed".</param>
/// <param name="State">"community", "active", "expiring" or "attention".</param>
/// <param name="Source">The entitlement source string (e.g. the configured snapshot source), for support.</param>
/// <param name="ValidUntil">The earliest expiry among granted capabilities, if any.</param>
/// <param name="Summary">A plain-language sentence the UI can show directly.</param>
public sealed record LicenceStatus(
    string Edition,
    string State,
    string Source,
    DateTimeOffset? ValidUntil,
    string Summary);

/// <summary>One paid capability and whether the tenant currently holds it.</summary>
public sealed record CapabilitySummary(
    string Capability,
    string Label,
    string Category,
    bool Enabled,
    string ReasonCode,
    string Source,
    DateTimeOffset? ValidUntil);
