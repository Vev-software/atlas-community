using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Vev.Atlas.Domain;
using Vev.Atlas.Domain.Portability;
using Vev.Atlas.Fabric;
using Vev.Atlas.Fabric.Dev;
using Vev.Atlas.Fabric.Portic;
using Vev.Fabric.Contracts.Entitlements;
using Vev.Atlas.Persistence;

namespace Vev.Atlas.Api;

/// <summary>Composition root for Atlas Community Edition. Wires the domain onto the Fabric shim.</summary>
public static class AtlasCommunityRegistration
{
    /// <summary>Register the Community catalogue services against the given SQLite connection string.</summary>
    public static IServiceCollection AddAtlasCommunity(this IServiceCollection services, string connectionString)
    {
        return AddAtlasCommunity(services, connectionString, null);
    }

    public static IServiceCollection AddAtlasCommunity(this IServiceCollection services, string connectionString, IConfiguration? configuration)
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
        services.AddScoped<ShadowItService>();
        // The open-core install boundary: any module install path runs its manifest through this guard,
        // which refuses a module declaring or satisfying a reserved paid capability (atlas#22).
        services.AddScoped<ModuleInstallGuard>();

        // UI extension seam (atlas#139/#140/#141): the host mounts entitlement-gated ui-extensions into
        // named client slots. The open-source client ships the slot + mount protocol only; the analysis
        // view content is delivered by a separate, entitlement-gated extension and never lives here. The
        // catalogue runs every registration through the install guard and then the entitlement gate, so a
        // module can never self-grant a paid capability and a denied extension is simply not offered.
        services.AddScoped<UiExtensionCatalog>();
        foreach (var registration in BuildUiExtensionRegistrations(configuration))
        {
            services.AddSingleton(registration);
        }

        // --- Portability format-adapter seam (issue #12) ---
        // The canonical atlas-contracts JSON adapter is always present. Community format modules
        // (ArchiMate/BPMN/report) add themselves by registering more ILandscapeExporter/Importer here.
        services.AddSingleton<ILandscapeExporter, AtlasJsonLandscapeExporter>();
        services.AddSingleton<ILandscapeImporter, AtlasJsonLandscapeImporter>();
        services.AddSingleton<ILandscapeImporter, AtlasMarkdownLandscapeImporter>();
        services.AddSingleton<LandscapeFormatRegistry>();

        // --- Portic AI provider extension (opt-in, issue #115) ---
        // When configuration is available and Atlas:Portic:BaseUrl is set, register the Portic provider
        // so it becomes available as a provider option without requiring a BYOK API key.
        if (configuration != null && !string.IsNullOrWhiteSpace(configuration.GetSection("Atlas:Portic:BaseUrl").Value))
        {
            services.AddPorticAiProvider(configuration);
        }

        return services;
    }

    /// <summary>
    /// The ui-extensions this host knows how to mount (atlas#139). Each is an edge module — its manifest
    /// declares no reserved paid capability, so it passes the open-core guard; whether it is actually
    /// offered to a tenant is the entitlement gate's decision, not the module's. The content source
    /// (<c>FragmentUrl</c>) is deployment configuration, so Community stays self-contained and carries no
    /// view of its own; when it is unset the extension is still gated correctly, just without content.
    /// </summary>
    private static IEnumerable<UiExtensionRegistration> BuildUiExtensionRegistrations(IConfiguration? configuration)
    {
        const string portfolioHealthId = "com.vev.atlas.portfolio-health";
        var portfolioHealthFragmentUrl = configuration?["Atlas:Extensions:PortfolioHealth:FragmentUrl"];

        yield return new UiExtensionRegistration(
            Id: portfolioHealthId,
            Slot: "landscape-right-rail",
            Title: "Portfolio health",
            RequiredCapability: AtlasCapabilities.PortfolioAnalysis,
            Manifest: ModuleManifest.ForEdgeModule(portfolioHealthId),
            FragmentUrl: string.IsNullOrWhiteSpace(portfolioHealthFragmentUrl) ? null : portfolioHealthFragmentUrl);
    }
}
