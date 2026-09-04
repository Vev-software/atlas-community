using Vev.Atlas.Fabric;

namespace Vev.Atlas.Domain;

/// <summary>
/// A ui-extension the Community host knows how to mount into a named slot (atlas#139). The host ships the
/// slot and the mount protocol only; the view content is delivered by a separate extension and is never
/// part of this open-source client. Whether the extension is actually offered to a tenant is an entitlement
/// decision (<see cref="RequiredCapability"/>), taken server-side and fail-closed — never by the client.
/// </summary>
/// <param name="Id">Stable extension id (reverse-DNS recommended), e.g. <c>com.vev.atlas.portfolio-health</c>.</param>
/// <param name="Slot">The named host slot this extension mounts into, e.g. <c>landscape-right-rail</c>.</param>
/// <param name="Title">Human-readable panel title the host renders around the mounted content.</param>
/// <param name="RequiredCapability">The reserved paid capability the tenant must hold for this to be offered.</param>
/// <param name="Manifest">The extension's open-core install manifest, run through <see cref="ModuleInstallGuard"/>.</param>
/// <param name="FragmentUrl">
/// Where the host loads the extension's content from (an external fragment endpoint). Null when no content
/// source is configured for this deployment — the extension may still be entitled, just not yet wired.
/// </param>
public sealed record UiExtensionRegistration(
    string Id,
    string Slot,
    string Title,
    CapabilityId RequiredCapability,
    ModuleManifest Manifest,
    string? FragmentUrl);

/// <summary>
/// A ui-extension that is installable and entitled for the current tenant — the host's mount offer. It
/// carries id + mount metadata only, never anything about capabilities the tenant does not hold.
/// </summary>
/// <param name="Kind">The closed-set extension shape identifier. For mountable UI panels this is <c>ui-extension</c>.</param>
/// <param name="Id">The extension id.</param>
/// <param name="Slot">The named slot to mount into.</param>
/// <param name="Title">The panel title.</param>
/// <param name="FragmentUrl">Where the host loads the content from, or null when unconfigured.</param>
public sealed record MountableUiExtension(string Kind, string Id, string Slot, string Title, string? FragmentUrl)
{
    public const string UiExtensionKind = "ui-extension";
}

/// <summary>
/// Resolves which registered ui-extensions the current tenant may mount (atlas#139, #140, #141). Every
/// registration is run through the open-core install guard first (so a module can never declare or satisfy
/// a reserved paid capability — the loader and the guard land together), and then through the entitlement
/// gate (so the paid line stays entitlement-only and fail-closed). Only extensions that pass both are
/// offered; a denied extension is simply omitted, so the surface never leaks anything about capabilities
/// the tenant does not hold.
/// </summary>
public sealed class UiExtensionCatalog(
    IEnumerable<UiExtensionRegistration> registrations,
    ModuleInstallGuard guard,
    PaidCapabilityGate gate)
{
    private readonly IReadOnlyList<UiExtensionRegistration> registrations = registrations.ToArray();

    /// <summary>The ui-extensions the current tenant is entitled to mount, across all slots.</summary>
    public async Task<IReadOnlyList<MountableUiExtension>> GetMountableAsync(CancellationToken ct = default)
    {
        var mountable = new List<MountableUiExtension>();

        foreach (var registration in registrations)
        {
            // The open-core guard runs on every offer: a module extends the edges but can never declare or
            // satisfy a reserved paid capability. A rejected module is never offered (fail-closed).
            try
            {
                await guard.EnsureInstallableAsync(registration.Manifest, ct);
            }
            catch (ModuleRejectedException)
            {
                continue;
            }

            // The paid line is entitlement-only and fail-closed: granting stays with the entitlement, not
            // the module. The client only ever reflects this decision — it can never take it.
            var decision = gate.Evaluate(
                registration.RequiredCapability,
                new ResourceId($"atlas:ui-extension/{registration.Id}"));
            if (!decision.Allowed)
            {
                continue;
            }

            mountable.Add(new MountableUiExtension(
                MountableUiExtension.UiExtensionKind,
                registration.Id,
                registration.Slot,
                registration.Title,
                registration.FragmentUrl));
        }

        return mountable;
    }
}
