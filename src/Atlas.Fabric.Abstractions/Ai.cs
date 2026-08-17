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
        Enabled &&
        ConsentAccepted &&
        !string.IsNullOrWhiteSpace(Provider) &&
        !string.IsNullOrWhiteSpace(ApiKey);
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
/// Minimal Fabric AI contract seam for grounded product assistance. Products depend on this provider-
/// neutral contract, never on a provider SDK directly.
/// </summary>
public interface IAiAssistService
{
    /// <summary>Produce grounded assistance for the given request, or report that no provider is configured.</summary>
    AiAssistResult Assist(AiAssistRequest request);
}
