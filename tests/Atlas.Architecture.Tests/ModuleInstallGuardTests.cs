using Vev.Atlas.Domain;
using Vev.Atlas.Fabric;
using Xunit;

namespace Vev.Atlas.Architecture.Tests;

/// <summary>
/// The open-core install boundary (atlas#22, engineering#3): a Community-installed module may not
/// declare or satisfy a reserved paid capability, so it can never become a back-door around the
/// entitlement gate. These tests pin the reserved set to the capabilities named in the issue and prove
/// the guard refuses (and audits) any module that claims one, while leaving edge modules free.
/// </summary>
public sealed class ModuleInstallGuardTests
{
    [Fact]
    public void The_reserved_paid_set_is_exactly_the_named_capabilities()
    {
        var reserved = AtlasCapabilities.ReservedPaid.Select(c => c.Value).OrderBy(v => v, StringComparer.Ordinal);
        string[] expected =
        [
            "atlas.ai.generate",
            "atlas.ai.review",
            "atlas.analysis.apm",
            "atlas.analysis.eol",
            "atlas.analysis.integration-map",
            "atlas.analysis.roadmap",
            "atlas.data.introspection",
            "atlas.data.overlap",
            "atlas.data.quality",
            "atlas.export.archimate",
        ];
        Assert.Equal(expected.OrderBy(v => v, StringComparer.Ordinal), reserved);
    }

    [Fact]
    public async Task A_module_that_claims_any_reserved_paid_capability_is_refused_and_audited()
    {
        foreach (var reserved in AtlasCapabilities.ReservedPaid)
        {
            var audit = new CollectingAuditSink();
            var guard = new ModuleInstallGuard(new FakeRequestContext("t-guard"), audit, TimeProvider.System);
            var manifest = new ModuleManifest("atlas.module.sneaky", [new CapabilityId("atlas.some.edge"), reserved]);

            var ex = await Assert.ThrowsAsync<ModuleRejectedException>(() => guard.EnsureInstallableAsync(manifest));

            Assert.Equal(AtlasReasonCodes.ReservedCapability, ex.Decision.ReasonCode);
            Assert.Contains(reserved, ex.ReservedCapabilities);
            var rejection = Assert.Single(audit.Events);
            Assert.Equal("atlas.module.rejected", rejection.Action);
            Assert.Equal("t-guard", rejection.Tenant.TenantId);
            Assert.Contains(reserved.Value, rejection.Resource.Value);
        }
    }

    [Fact]
    public async Task An_edge_module_with_no_reserved_capability_installs_cleanly()
    {
        var audit = new CollectingAuditSink();
        var guard = new ModuleInstallGuard(new FakeRequestContext("t-ok"), audit, TimeProvider.System);

        // A pure format adapter (no declared capabilities) and a module declaring only a non-reserved one.
        await guard.EnsureInstallableAsync(ModuleManifest.ForEdgeModule("atlas.module.archimate"));
        await guard.EnsureInstallableAsync(new ModuleManifest("atlas.module.report", [new CapabilityId("atlas.module.report.export")]));

        Assert.Empty(audit.Events);   // nothing refused, nothing to audit
    }

    private sealed class FakeRequestContext(string tenant) : IRequestContextAccessor
    {
        public TenantContext Tenant { get; } = new(tenant);
        public PrincipalContext Principal { get; } = new("installer", "Installer", ["AtlasArchitect"]);
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
