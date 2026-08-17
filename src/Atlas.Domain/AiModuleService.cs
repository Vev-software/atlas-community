using Vev.Atlas.Fabric;
using Vev.Fabric.Contracts.Audit;

namespace Vev.Atlas.Domain;

/// <summary>
/// Authorized product-side AI module management. Keeps consent, enablement and BYOK state server-side and
/// exposes only redaction-safe status back to the UI.
/// </summary>
public sealed class AiModuleService(
    IRequestContextAccessor context,
    IAuthorizer authorizer,
    IAiModuleConfigurationStore store,
    AiAllowanceService allowances,
    IAtlasAuditSink audit,
    TimeProvider clock)
{
    private static readonly ResourceId ModuleResource = new("atlas:ai/module");

    public async Task<AiModuleStatus> GetStatusAsync(CancellationToken ct = default)
    {
        AuthorizeRead();

        var tenant = context.Tenant;
        var configuration = await store.GetAsync(tenant, ct);
        var allowance = allowances.Describe(AtlasCapabilities.AiStructure, new ResourceId("atlas:structure-draft"));

        return new AiModuleStatus(
            Enabled: configuration?.Enabled == true,
            ConsentAccepted: configuration?.ConsentAccepted == true,
            Provider: configuration?.Provider,
            ApiKeyConfigured: !string.IsNullOrWhiteSpace(configuration?.ApiKey),
            Ready: configuration?.IsUsable == true,
            CanManage: authorizer.Authorize(tenant, context.Principal, AtlasActions.AssetWrite, ModuleResource).Allowed,
            ConsentAcceptedAt: configuration?.ConsentAcceptedAt,
            ConsentAcceptedBy: configuration?.ConsentAcceptedBy,
            Allowance: allowance);
    }

    public async Task<AiModuleStatus> SaveAsync(AiModuleSaveRequest request, CancellationToken ct = default)
    {
        AuthorizeWrite();

        if (!request.ConsentAccepted)
        {
            throw new CatalogueValidationException("Accept the Atlas AI consent step before enabling the module.");
        }

        if (!request.Enabled)
        {
            throw new CatalogueValidationException("The AI module must be enabled when saving setup.");
        }

        var provider = NormalizeProvider(request.Provider);
        if (provider is null)
        {
            throw new CatalogueValidationException("Select a supported provider.");
        }

        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            throw new CatalogueValidationException("Paste an API key for the selected provider.");
        }

        var now = clock.GetUtcNow();
        await store.SaveAsync(
            context.Tenant,
            new AiModuleConfiguration(
                Enabled: true,
                ConsentAccepted: true,
                ConsentAcceptedAt: now,
                ConsentAcceptedBy: context.Principal.PrincipalId,
                Provider: provider,
                ApiKey: request.ApiKey.Trim(),
                UpdatedAt: now),
            ct);

        await audit.WriteAsync(
            AtlasAudit.Event(context, clock, "atlas.ai.module.updated", ModuleResource.Value, AuditCategory.Admin),
            ct);

        return await GetStatusAsync(ct);
    }

    public async Task DisableAsync(CancellationToken ct = default)
    {
        AuthorizeWrite();

        await store.SaveAsync(
            context.Tenant,
            new AiModuleConfiguration(
                Enabled: false,
                ConsentAccepted: false,
                ConsentAcceptedAt: null,
                ConsentAcceptedBy: null,
                Provider: null,
                ApiKey: null,
                UpdatedAt: clock.GetUtcNow()),
            ct);

        await audit.WriteAsync(
            AtlasAudit.Event(context, clock, "atlas.ai.module.disabled", ModuleResource.Value, AuditCategory.Admin),
            ct);
    }

    private void AuthorizeRead()
    {
        var decision = authorizer.Authorize(context.Tenant, context.Principal, AtlasActions.AssetRead, ModuleResource);
        if (!decision.Allowed)
        {
            throw AccessDeniedException.FromAuthorization(decision, $"'{AtlasActions.AssetRead}' denied ({decision.ReasonCode}).");
        }
    }

    private void AuthorizeWrite()
    {
        var decision = authorizer.Authorize(context.Tenant, context.Principal, AtlasActions.AssetWrite, ModuleResource);
        if (!decision.Allowed)
        {
            throw AccessDeniedException.FromAuthorization(decision, $"'{AtlasActions.AssetWrite}' denied ({decision.ReasonCode}).");
        }
    }

    private static string? NormalizeProvider(string? provider)
    {
        var value = provider?.Trim().ToLowerInvariant();
        return value switch
        {
            AiProviders.OpenAi => AiProviders.OpenAi,
            AiProviders.Anthropic => AiProviders.Anthropic,
            _ => null
        };
    }
}

/// <summary>Safe client-facing shape for the current AI module state.</summary>
public sealed record AiModuleStatus(
    bool Enabled,
    bool ConsentAccepted,
    string? Provider,
    bool ApiKeyConfigured,
    bool Ready,
    bool CanManage,
    DateTimeOffset? ConsentAcceptedAt,
    string? ConsentAcceptedBy,
    AiAllowanceSnapshot Allowance);

/// <summary>Server-side AI setup payload.</summary>
public sealed record AiModuleSaveRequest(
    bool Enabled,
    bool ConsentAccepted,
    string? Provider,
    string? ApiKey);

/// <summary>Stable provider ids exposed to the product UI.</summary>
public static class AiProviders
{
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";

    public static readonly IReadOnlyList<string> All = [OpenAi, Anthropic];
}
