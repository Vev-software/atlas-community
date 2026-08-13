using Vev.Atlas.Domain;
using Vev.Atlas.Fabric;
using Vev.Atlas.Fabric.Dev;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// Paid data-architecture seams are reserved in the public taxonomy before the features exist; in
/// Community the empty grant snapshot must therefore deny each one through the normal entitlement gate.
/// </summary>
public sealed class DataArchitectureSeamTests
{
    [Fact]
    public void Reserved_data_architecture_capabilities_are_entitlement_denied_in_community()
    {
        var gate = new PaidCapabilityGate(new FakeRequestContext(), CommunityEntitlementService.Community);

        foreach (var capability in ReservedDataArchitectureCapabilities)
        {
            var decision = gate.Evaluate(capability, new ResourceId("atlas:asset/data-architecture"));

            Assert.False(decision.Allowed);
            Assert.Equal(ReasonCodes.EntitlementDenied, decision.ReasonCode);
        }
    }

    private static readonly CapabilityId[] ReservedDataArchitectureCapabilities =
    [
        AtlasCapabilities.DataIntrospection,
        AtlasCapabilities.DataOverlap,
        AtlasCapabilities.DataQuality,
        AtlasCapabilities.ArchimateExport,
    ];

    private sealed class FakeRequestContext : IRequestContextAccessor
    {
        public TenantContext Tenant { get; } = new("t-paid");
        public PrincipalContext Principal { get; } = new("arch", "Architect", [AtlasRoles.Architect]);
    }
}
