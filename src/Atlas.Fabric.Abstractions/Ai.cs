using Microsoft.Extensions.DependencyInjection;

namespace Vev.Atlas.Fabric;

/// <summary>
/// A grounded AI-assist request routed through the Fabric AI contract. The product supplies the stable
/// capability id, purpose and grounding facts; Fabric supplies the provider implementation when one is
/// configured (handbook 10, 11 §4).
/// </summary>
/// <param name="Tenant">The tenant whose catalogue context grounds the request.</param>
/// <param name="Principal">The principal asking for assistance.</param>
/// <param name="Capability">The metered <c>atlas.ai.*</c> capability in use.</param>
/// <param name="Purpose">Short purpose label for the assist.</param>
/// <param name="Grounding">Grounded, product-supplied facts the assistant may speak from.</param>
/// <param name="Attachments">Optional multimodal attachments supplied by the product, e.g. an uploaded image.</param>
/// <param name="Resource">Optional resource or scope the assist applies to.</param>
public readonly record struct AiAssistRequest(
    TenantContext Tenant,
    PrincipalContext Principal,
    CapabilityId Capability,
    string Purpose,
    string Grounding,
    IReadOnlyList<AiAssistAttachment>? Attachments = null,
    ResourceId? Resource = null);

/// <summary>
/// Optional multimodal attachment routed through the Fabric AI contract. The product provides stable
/// metadata plus opaque base64 content; Fabric decides whether a configured provider can use it.
/// </summary>
/// <param name="Name">Caller-supplied attachment name, e.g. a filename.</param>
/// <param name="ContentType">Media type, e.g. <c>image/png</c>.</param>
/// <param name="ContentBase64">Opaque base64 payload. Products must not log or audit it by default.</param>
public readonly record struct AiAssistAttachment(
    string Name,
    string ContentType,
    string ContentBase64);

/// <summary>
/// The result of a Fabric AI-assist call. Community may legitimately run with no provider configured, in
/// which case the product falls back to a deterministic local experience rather than failing hard.
/// </summary>
/// <param name="Configured">Whether an AI provider is configured behind the Fabric contract.</param>
/// <param name="Message">Optional assistant-generated message.</param>
/// <param name="Source">Where the result came from.</param>
public readonly record struct AiAssistResult(bool Configured, string? Message, string Source)
{
    /// <summary>An assistant-generated result.</summary>
    public static AiAssistResult Available(string message, string source) => new(true, message, source);

    /// <summary>No configured AI provider behind the Fabric contract; the product should degrade cleanly.</summary>
    public static AiAssistResult Unavailable(string source) => new(false, null, source);
}

/// <summary>
/// Provider-neutral AI module configuration owned by the product and consumed behind the Fabric AI seam.
/// Community persists this per tenant so a self-hoster can bring their own provider key without exposing it
/// to Atlas hosting.
/// </summary>
public sealed record AiModuleConfiguration(
    bool Enabled,
    bool ConsentAccepted,
    DateTimeOffset? ConsentAcceptedAt,
    string? ConsentAcceptedBy,
    string? Provider,
    string? ApiKey,
    DateTimeOffset? UpdatedAt)
{
    /// <summary>Whether the module is ready to serve requests through the Fabric AI contract.</summary>
    public bool IsUsable =>
        IsUsableForProvider(null);

    /// <summary>
    /// Whether the module is ready to serve requests. For extension providers (identified by
    /// <paramref name="extensionProviderIds"/>), an API key is not required — the extension handles
    /// its own authentication. Built-in providers (OpenAI, Anthropic) still require a key.
    /// </summary>
    public bool IsUsableForProvider(IReadOnlyList<string>? extensionProviderIds) =>
        Enabled &&
        ConsentAccepted &&
        !string.IsNullOrWhiteSpace(Provider) &&
        (!string.IsNullOrWhiteSpace(ApiKey) ||
         (extensionProviderIds != null && extensionProviderIds.Contains(Provider!)));
}

/// <summary>
/// Product-owned store for the current tenant's AI module settings. Fabric implementations consume the
/// provider-neutral record; products own the admin UX and persistence details.
/// </summary>
public interface IAiModuleConfigurationStore
{
    /// <summary>Read the tenant's current AI module configuration, or null when nothing has been set up yet.</summary>
    ValueTask<AiModuleConfiguration?> GetAsync(TenantContext tenant, CancellationToken cancellationToken = default);

    /// <summary>Persist the tenant's AI module configuration.</summary>
    ValueTask SaveAsync(
        TenantContext tenant,
        AiModuleConfiguration configuration,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Extension point for third-party AI providers. Each implementation declares a stable provider id
/// (e.g. "portic") and handles <c>AiAssistRequest</c> instances routed to it. The interface lives in
/// the abstractions layer so extensions do not need to depend on the Community runtime.
/// </summary>
public interface IAiProviderExtension
{
    /// <summary>Stable, lowercase provider identifier used for configuration and routing (e.g. "portic").</summary>
    string ProviderId { get; }

    /// <summary>Produce grounded assistance, or return <c>AiAssistResult.Unavailable</c> when the provider cannot serve the request.</summary>
    AiAssistResult Assist(AiAssistRequest request);
}

/// <summary>
/// Helper to discover registered <c>IAiProviderExtension</c> instances from the service provider and
/// build a lookup by provider id. Used by the assist service and provider validation.
/// </summary>
public static class AiProviderExtensions
{
    /// <summary>Resolve all registered provider extensions from the service provider.</summary>
    public static IReadOnlyList<IAiProviderExtension> GetExtensions(IServiceProvider sp) =>
        (sp.GetService(typeof(IEnumerable<IAiProviderExtension>)) as IEnumerable<IAiProviderExtension>)?.ToList() ?? [];

    /// <summary>Find the extension for the given provider id, or <c>null</c> if none is registered.</summary>
    public static IAiProviderExtension? FindByProviderId(IServiceProvider sp, string providerId) =>
        GetExtensions(sp).FirstOrDefault(e => e.ProviderId == providerId);
}

/// <summary>
/// Minimal Fabric AI contract seam for grounded product assistance. Products depend on this provider-
/// neutral contract, never on a provider SDK directly.
/// </summary>
public interface IAiAssistService
{
    /// <summary>Produce grounded assistance for the given request, or report that no provider is configured.</summary>
    AiAssistResult Assist(AiAssistRequest request);
}
