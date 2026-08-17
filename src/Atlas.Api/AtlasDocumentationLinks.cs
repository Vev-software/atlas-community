namespace Vev.Atlas.Api;

/// <summary>
/// Stable documentation anchor registry for the Community shell. Keeps UI deep links centralized and
/// deployment-aware via <see cref="AtlasUrls"/>.
/// </summary>
public static class AtlasDocumentationLinks
{
    public static string Resolve(AtlasUrls urls, string key) =>
        key switch
        {
            "atlas-ai-setup" => urls.DocumentationUrl("/atlas/ai.md#atlas-ai-setup"),
            "atlas-ai-chat" => urls.DocumentationUrl("/atlas/ai.md#atlas-ai-chat"),
            _ => urls.DocumentationUrl("/README.md")
        };
}
