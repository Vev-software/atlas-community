using Vev.Atlas.Fabric;

namespace Vev.Atlas.Domain;

/// <summary>
/// What a Community module declares about itself at install time — the Atlas-side shape of the Fabric
/// extension manifest (fabric#10). A module adds value at the edges (importers/exporters, connectors, UI
/// panels) through the capabilities it declares or satisfies; the install guard is what stops that from
/// becoming a back-door around the paid line.
/// </summary>
/// <param name="Id">Stable module identifier, e.g. <c>atlas.module.archimate</c>.</param>
/// <param name="Capabilities">
/// The capabilities the module declares <i>or</i> claims to satisfy. Both reduce to the same check: a
/// module may not associate itself with a reserved paid capability.
/// </param>
public sealed record ModuleManifest(string Id, IReadOnlyCollection<CapabilityId> Capabilities)
{
    /// <summary>A module that declares no capabilities (the common case: a pure format adapter).</summary>
    public static ModuleManifest ForEdgeModule(string id) => new(id, []);
}

/// <summary>
/// Thrown when a module is refused installation because it declares or satisfies a reserved paid
/// capability. Carries the denying <see cref="Fabric.Decision"/> (reason code + source) so the refusal is
/// machine-readable, never a bare error, and the offending capabilities so the author can see what to drop.
/// </summary>
public sealed class ModuleRejectedException(Decision decision, IReadOnlyCollection<CapabilityId> reservedCapabilities, string message)
    : Exception(message)
{
    /// <summary>The denying decision, including the <c>reserved_capability</c> reason code.</summary>
    public Decision Decision { get; } = decision;

    /// <summary>The reserved paid capabilities the module tried to claim.</summary>
    public IReadOnlyCollection<CapabilityId> ReservedCapabilities { get; } = reservedCapabilities;
}

/// <summary>
/// The open-core install boundary (atlas#22, engineering#3). Community may install modules, but the
/// free/paid line stays <b>entitlement-only</b>: a module may not declare or satisfy a reserved paid
/// capability (<see cref="AtlasCapabilities.ReservedPaid"/>), so it can never flip a paid capability to
/// allowed — that decision stays with <see cref="PaidCapabilityGate"/> → the Fabric entitlement service.
/// Any module install path must run its manifest through this guard before the module is loaded; a
/// violation is refused with a reason code and audited. Enforces, at the Atlas boundary, the guard
/// defined generically in fabric#10.
/// </summary>
public sealed class ModuleInstallGuard(IRequestContextAccessor context, IAuditSink audit, TimeProvider clock)
{
    private const string Source = "module-install-guard";

    /// <summary>
    /// Verify a module manifest is installable in Community. No-op when the manifest is clean; throws
    /// <see cref="ModuleRejectedException"/> (and emits an <c>atlas.module.rejected</c> audit event) when
    /// it declares or satisfies any reserved paid capability.
    /// </summary>
    public async Task EnsureInstallableAsync(ModuleManifest manifest, CancellationToken ct = default)
    {
        var reserved = manifest.Capabilities.Where(AtlasCapabilities.IsReservedPaid).Distinct().ToArray();
        if (reserved.Length == 0)
        {
            return;
        }

        var decision = Decision.Deny(ReasonCodes.ReservedCapability, Source);
        await EmitRejectionAsync(manifest, reserved, ct);

        var claimed = string.Join(", ", reserved.Select(c => c.Value));
        throw new ModuleRejectedException(decision, reserved,
            $"Module '{manifest.Id}' cannot be installed in Community: it declares or satisfies reserved paid " +
            $"capability/ies ({claimed}). Modules add value at the edges; paid capabilities stay behind the " +
            "Fabric entitlement gate (atlas#22).");
    }

    private ValueTask EmitRejectionAsync(ModuleManifest manifest, IReadOnlyCollection<CapabilityId> reserved, CancellationToken ct)
    {
        // No secrets, no customer content — the module id, the action, and the reserved ids it claimed (E4/E5).
        var evt = new AuditEvent(
            TenantId: context.Tenant.TenantId,
            ActorPrincipalId: context.Principal.PrincipalId,
            Action: "atlas.module.rejected",
            Resource: $"atlas:module/{manifest.Id}?reserved={string.Join('+', reserved.Select(c => c.Value))}",
            OccurredAt: clock.GetUtcNow(),
            CorrelationId: Guid.NewGuid().ToString("N"));
        return audit.WriteAsync(evt, ct);
    }
}
