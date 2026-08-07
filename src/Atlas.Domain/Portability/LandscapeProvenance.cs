using System.Reflection;
using Vev.Atlas.Contracts;

namespace Vev.Atlas.Domain.Portability;

/// <summary>
/// Provenance stamped onto exported landscapes: what produced the document. Held metadata (name +
/// version), never analysis — it lets a consumer know a bundle came from Atlas Community, and which
/// build, when reasoning about contract-version compatibility.
/// </summary>
public static class LandscapeProvenance
{
    /// <summary>The producing tool name carried in every export.</summary>
    public const string ProducerName = "Atlas Community";

    /// <summary>The <see cref="Generator"/> stamped onto exports from this build.</summary>
    public static Generator Generator { get; } = new(ProducerName, ProducerVersion());

    private static string? ProducerVersion() =>
        typeof(LandscapeProvenance).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(LandscapeProvenance).Assembly.GetName().Version?.ToString();
}
