using Vev.Atlas.Fabric;

namespace Vev.Atlas.Domain;

public static class UiExtensionContracts
{
    public const string ExtensionsContractVersion = "1";
    public const string FragmentMountKind = "fragment";
    public const string FragmentMountContractVersion = "1";

    public static bool Supports(UiExtensionMount mount) =>
        string.Equals(mount.Kind, FragmentMountKind, StringComparison.Ordinal) &&
        string.Equals(mount.ContractVersion, FragmentMountContractVersion, StringComparison.Ordinal);
}

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
/// <param name="Mount">The typed mount contract the host offers. Unknown kinds or versions are unsupported.</param>
public sealed record UiExtensionRegistration(
    string Id,
    string Slot,
    string Title,
    CapabilityId RequiredCapability,
    ModuleManifest Manifest,
    UiExtensionMount Mount);

/// <summary>
/// The typed mount contract for one offered ui-extension. V1 supports one shape only: a sandboxed fragment
/// mounted in an iframe. Future kinds must be added explicitly; unknown kinds or versions stay unsupported.
/// </summary>
/// <param name="Kind">The mount shape identifier. V1 supports <c>fragment</c> only.</param>
/// <param name="ContractVersion">The version of the mount-shape contract.</param>
/// <param name="Url">
/// The fragment URL to load inside the sandboxed iframe. Null when the extension is entitled but no content
/// source is configured for this deployment yet.
/// </param>
public sealed record UiExtensionMount(string Kind, string ContractVersion, string? Url)
{
    public static UiExtensionMount Fragment(string? url) =>
        new(UiExtensionContracts.FragmentMountKind, UiExtensionContracts.FragmentMountContractVersion, url);
}

/// <summary>
/// A ui-extension that is installable and entitled for the current tenant — the host's mount offer. It
/// carries id + mount metadata only, never anything about capabilities the tenant does not hold.
/// </summary>
/// <param name="Id">The extension id.</param>
/// <param name="Slot">The named slot to mount into.</param>
/// <param name="Title">The panel title.</param>
/// <param name="Mount">The typed mount metadata the host understands for this extension.</param>
public sealed record MountableUiExtension(string Id, string Slot, string Title, UiExtensionMount Mount);

/// <summary>
/// The versioned response envelope for <c>GET /api/v1/extensions/ui</c>.
/// </summary>
/// <param name="ContractVersion">The public host-offer contract version.</param>
/// <param name="Extensions">The entitled, installable ui-extensions the host can mount for the tenant.</param>
public sealed record UiExtensionListResponse(string ContractVersion, IReadOnlyList<MountableUiExtension> Extensions);

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

            if (!UiExtensionContracts.Supports(registration.Mount))
            {
                continue;
            }

            mountable.Add(new MountableUiExtension(
                registration.Id,
                registration.Slot,
                registration.Title,
                registration.Mount));
        }

        return mountable;
    }
}
