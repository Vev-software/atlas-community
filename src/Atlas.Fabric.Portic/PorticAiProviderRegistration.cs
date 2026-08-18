using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Fabric.Portic;

/// <summary>
/// Extension methods to register the Portic AI provider extension. Call <c>AddPorticAiProvider()</c>
/// to opt-in. Without this call, Portic is invisible to the runtime.
/// </summary>
public static class PorticAiProviderRegistration
{
    /// <summary>
    /// Register the Portic AI provider extension. Reads configuration from the "Atlas:Portic" section.
    /// The provider becomes available for routing when a tenant's AI module is configured with
    /// provider = "portic".
    /// </summary>
    public static IServiceCollection AddPorticAiProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PorticOptions>(configuration.GetSection(PorticOptions.SectionName));
        services.AddHttpClient<PorticAiProviderExtension>();
        services.AddSingleton<IAiProviderExtension, PorticAiProviderExtension>();
        return services;
    }
}
