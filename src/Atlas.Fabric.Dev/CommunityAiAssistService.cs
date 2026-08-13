using Vev.Atlas.Fabric;

namespace Vev.Atlas.Fabric.Dev;

/// <summary>
/// Community default for the Fabric AI contract: no provider configured. The product must therefore
/// degrade to a deterministic local fallback rather than making setup or browse dependent on AI.
/// </summary>
public sealed class CommunityAiAssistService : IAiAssistService
{
    private const string Source = "ai:unconfigured";

    /// <summary>Singleton no-provider evaluator for Community.</summary>
    public static CommunityAiAssistService Unconfigured { get; } = new();

    /// <inheritdoc />
    public AiAssistResult Assist(AiAssistRequest request) => AiAssistResult.Unavailable(Source);
}
