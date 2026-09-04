using Vev.Atlas.Domain;
using Vev.Atlas.Fabric;
using Vev.Atlas.Fabric.Dev;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// The host-side chain that decides whether a ui-extension may be mounted (atlas#139/#140/#141): every
/// registration is run through the open-core install guard (a module can never self-declare a reserved paid
/// capability) and then the entitlement gate (the paid line is entitlement-only and fail-closed). Only
/// extensions that pass both are offered, and a denied one is simply omitted — the surface never leaks a
/// capability the tenant does not hold.
/// </summary>
public sealed class UiExtensionCatalogTests
{
    private const string PortfolioHealthId = "com.vev.atlas.portfolio-health";

    [Fact]
    public async Task An_entitled_edge_extension_is_offered_with_its_mount_metadata()
    {
        var catalog = BuildCatalog(
            granted: new HashSet<string>(StringComparer.Ordinal) { AtlasCapabilities.PortfolioAnalysis.Value },
            audit: out _,
            registrations: PortfolioHealth("https://enterprise.local/portfolio-health"));

        var mountable = await catalog.GetMountableAsync();

        var offer = Assert.Single(mountable);
        Assert.Equal(MountableUiExtension.UiExtensionKind, offer.Kind);
        Assert.Equal(PortfolioHealthId, offer.Id);
        Assert.Equal("landscape-right-rail", offer.Slot);
        Assert.Equal("Portfolio health", offer.Title);
        Assert.Equal("https://enterprise.local/portfolio-health", offer.FragmentUrl);
    }

    [Fact]
    public async Task Without_the_entitlement_the_extension_is_not_offered()
    {
        // The default Community grant set is empty, so the gate denies — the tile stays behind the paid line.
        var catalog = BuildCatalog(
            granted: new HashSet<string>(StringComparer.Ordinal),
            audit: out _,
            registrations: PortfolioHealth("https://enterprise.local/portfolio-health"));

        var mountable = await catalog.GetMountableAsync();

        Assert.Empty(mountable);
    }

    [Fact]
    public async Task A_module_that_declares_the_paid_capability_is_rejected_by_the_guard_and_never_offered()
    {
        // Even with the entitlement granted, a module that tries to *declare* the reserved paid capability is
        // refused by the open-core guard — granting stays with the entitlement, never the module.
        var sneaky = new UiExtensionRegistration(
            Id: "com.vev.atlas.sneaky",
            Slot: "landscape-right-rail",
            Title: "Sneaky",
            RequiredCapability: AtlasCapabilities.PortfolioAnalysis,
            Manifest: new ModuleManifest("com.vev.atlas.sneaky", [AtlasCapabilities.PortfolioAnalysis]),
            FragmentUrl: "https://enterprise.local/sneaky");

        var catalog = BuildCatalog(
            granted: new HashSet<string>(StringComparer.Ordinal) { AtlasCapabilities.PortfolioAnalysis.Value },
            audit: out var audit,
            registrations: sneaky);

        var mountable = await catalog.GetMountableAsync();

        Assert.Empty(mountable);
        // The guard was exercised: the blocked install is an audited, governed decision.
        var rejection = Assert.Single(audit.Events);
        Assert.Equal("atlas.module.rejected", rejection.Action);
    }

    private static UiExtensionRegistration PortfolioHealth(string? fragmentUrl) => new(
        Id: PortfolioHealthId,
        Slot: "landscape-right-rail",
        Title: "Portfolio health",
        RequiredCapability: AtlasCapabilities.PortfolioAnalysis,
        Manifest: ModuleManifest.ForEdgeModule(PortfolioHealthId),
        FragmentUrl: fragmentUrl);

    private static UiExtensionCatalog BuildCatalog(
        IReadOnlySet<string> granted,
        out CollectingAuditSink audit,
        params UiExtensionRegistration[] registrations)
    {
        var context = new FakeRequestContext();
        audit = new CollectingAuditSink();
        var guard = new ModuleInstallGuard(context, audit, TimeProvider.System);
        var gate = new PaidCapabilityGate(context, new CommunityEntitlementService(granted));
        return new UiExtensionCatalog(registrations, guard, gate);
    }

    private sealed class FakeRequestContext : IRequestContextAccessor
    {
        public TenantContext Tenant { get; } = new("t-ext");
        public PrincipalContext Principal { get; } = new("viewer", "Viewer", [AtlasRoles.Customer]);
        public string CorrelationId { get; } = "test-correlation";
    }

    private sealed class CollectingAuditSink : IAtlasAuditSink
    {
        public List<AuditEvent> Events { get; } = [];

        public ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }
}
