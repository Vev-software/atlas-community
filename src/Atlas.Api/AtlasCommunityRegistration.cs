using Microsoft.EntityFrameworkCore;
using Vev.Atlas.Domain;
using Vev.Atlas.Domain.Portability;
using Vev.Atlas.Fabric;
using Vev.Atlas.Fabric.Dev;
using Vev.Fabric.Contracts.Entitlements;
using Vev.Atlas.Persistence;

namespace Vev.Atlas.Api;

/// <summary>Composition root for Atlas Community Edition. Wires the domain onto the Fabric shim.</summary>
public static class AtlasCommunityRegistration
{
    /// <summary>Register the Community catalogue services against the given SQLite connection string.</summary>
    public static IServiceCollection AddAtlasCommunity(this IServiceCollection services, string connectionString)
    {
        // --- Fabric shim (dev implementations; swap for Vev.Fabric.* when it lands, handbook 11 §4) ---
        var contextAccessor = new AmbientRequestContextAccessor();
        services.AddSingleton(contextAccessor);
        services.AddSingleton<IRequestContextAccessor>(contextAccessor);

        // Atlas declares its own role→permission definitions on top of the Fabric authz mechanism (11 §4).
        // A full-landscape export is an elevated action: read-only customers may browse but not bulk-export
        // the whole map (atlas#36).
        var policies = new AuthorizationPolicyRegistry()
            .Require(AtlasActions.AssetWrite, AtlasRoles.Architect)
            .Require(AtlasActions.LandscapeExport, AtlasRoles.Architect);
        services.AddSingleton(policies);
        services.AddSingleton<IAuthorizer, DevAuthorizer>();

        // Consume the public Fabric entitlement contract locally: request-path evaluation stays local and
        // fail-static, while the snapshot source is configured per deployment (atlas#21, fabric#4).
        services.AddHttpClient(EntitlementSnapshotRefreshService.HttpClientName);
        services.AddSingleton<CommunityEntitlementService>();
        services.AddSingleton<IEntitlementService>(sp => sp.GetRequiredService<CommunityEntitlementService>());
        services.AddSingleton<IEntitlementAllowanceProvider>(sp => sp.GetRequiredService<CommunityEntitlementService>());
        services.AddHostedService<EntitlementSnapshotRefreshService>();

        services.AddSingleton<InMemoryAuditSink>();
        services.AddSingleton<IAtlasAuditSink>(sp => sp.GetRequiredService<InMemoryAuditSink>());
        services.AddSingleton<IAuditQueryService>(sp => sp.GetRequiredService<InMemoryAuditSink>());

        services.AddSingleton(TimeProvider.System);

        // --- Persistence (thin storage behind the repository port) ---
        services.AddDbContext<AtlasDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<IAssetRepository, EfAssetRepository>();
        services.AddScoped<IAiModuleConfigurationStore, EfAiModuleConfigurationStore>();
        services.AddScoped<IAiAssistService, CommunityAiAssistService>();

        // --- Domain ---
        services.AddScoped<AssetService>();
        services.AddScoped<ContextPackService>();
        services.AddScoped<StructureDraftService>();
        services.AddScoped<DeliverableDraftService>();
        services.AddScoped<AiAllowanceService>();
        services.AddScoped<AiModuleService>();
        services.AddScoped<LandscapeChatService>();
        services.AddScoped<McpReadService>();
        services.AddScoped<PaidCapabilityGate>();
        services.AddScoped<SetupCopilotService>();
        // The open-core install boundary: any module install path runs its manifest through this guard,
        // which refuses a module declaring or satisfying a reserved paid capability (atlas#22).
        services.AddScoped<ModuleInstallGuard>();

        // --- Portability format-adapter seam (issue #12) ---
        // The canonical atlas-contracts JSON adapter is always present. Community format modules
        // (ArchiMate/BPMN/report) add themselves by registering more ILandscapeExporter/Importer here.
        services.AddSingleton<ILandscapeExporter, AtlasJsonLandscapeExporter>();
        services.AddSingleton<ILandscapeImporter, AtlasJsonLandscapeImporter>();
        services.AddSingleton<LandscapeFormatRegistry>();

        return services;
    }
}
