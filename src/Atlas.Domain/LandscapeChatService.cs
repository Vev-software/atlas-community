using System.Text;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Domain;

/// <summary>
/// Grounded, read-only Q&A over the current tenant landscape. Uses the Fabric AI contract and the existing
/// MCP read surface for grounding; when no provider is configured, the caller gets a typed setup-required
/// response rather than a blind error.
/// </summary>
public sealed class LandscapeChatService(
    IRequestContextAccessor context,
    IAuthorizer authorizer,
    IAiAssistService aiAssist,
    IAiModuleConfigurationStore moduleStore,
    McpReadService mcp,
    IAtlasAuditSink audit,
    TimeProvider clock)
{
    private static readonly ResourceId ChatResource = new("atlas:ai/chat");

    public async Task<LandscapeChatReply> AskAsync(LandscapeChatRequest request, CancellationToken ct = default)
    {
        AuthorizeRead();

        var normalizedQuestion = request.Question?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuestion))
        {
            throw new CatalogueValidationException("Ask a question about the current landscape.");
        }

        var configuration = await moduleStore.GetAsync(context.Tenant, ct);
        if (configuration?.IsUsable != true)
        {
            return LandscapeChatReply.SetupRequired("The Atlas AI module is not enabled for this tenant yet.");
        }

        var selectedAssetIds = (request.AssetIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (selectedAssetIds.Length == 0)
        {
            var topMatches = await mcp.SearchAssetsAsync(normalizedQuestion, kind: null, ct);
            selectedAssetIds = topMatches.Take(6).Select(a => a.Id).ToArray();

            if (selectedAssetIds.Length == 0)
            {
                selectedAssetIds = (await mcp.SearchAssetsAsync(query: null, kind: null, ct))
                    .Take(6)
                    .Select(a => a.Id)
                    .ToArray();
            }
        }

        var pack = await mcp.ExportContextPackAsync(selectedAssetIds, relationshipIds: [], ct);
        var grounding = BuildGrounding(normalizedQuestion, pack);
        var assist = aiAssist.Assist(new AiAssistRequest(
            context.Tenant,
            context.Principal,
            AtlasCapabilities.AiChat,
            "atlas-landscape-chat",
            grounding,
            Resource: ChatResource));

        if (!assist.Configured || string.IsNullOrWhiteSpace(assist.Message))
        {
            return LandscapeChatReply.SetupRequired("Atlas AI is configured incompletely for this tenant.");
        }

        await audit.WriteAsync(
            AtlasAudit.Event(context, clock, AtlasCapabilities.AiChat.Value, ChatAuditResource(pack).Value),
            ct);

        return new LandscapeChatReply(
            Status: "ready",
            Message: assist.Message!,
            Source: assist.Source,
            SelectedAssetIds: pack.SelectedAssetIds.ToArray(),
            DocLinks:
            [
                new AiDocLink("Set up Atlas AI", "atlas-ai-setup"),
                new AiDocLink("Chat with your landscape", "atlas-ai-chat")
            ]);
    }

    private void AuthorizeRead()
    {
        var decision = authorizer.Authorize(context.Tenant, context.Principal, AtlasActions.AssetRead, ChatResource);
        if (!decision.Allowed)
        {
            throw AccessDeniedException.FromAuthorization(decision, $"'{AtlasActions.AssetRead}' denied ({decision.ReasonCode}).");
        }
    }

    private static string BuildGrounding(string question, McpContextPack pack)
    {
        var text = new StringBuilder();
        text.AppendLine("Answer only from the grounded Atlas context below.");
        text.AppendLine("If the answer is not supported by the grounded facts, say that the landscape does not currently show it.");
        text.AppendLine("Do not propose any write or automatic change.");
        text.AppendLine();
        text.AppendLine("User question:");
        text.AppendLine(question);
        text.AppendLine();
        text.AppendLine("Grounded context pack summary:");
        text.AppendLine(pack.Summary);
        text.AppendLine();
        text.AppendLine("Grounded context pack markdown:");
        text.AppendLine(pack.Markdown);
        return text.ToString();
    }

    private static ResourceId ChatAuditResource(McpContextPack pack) =>
        new($"atlas:ai/chat?selectedAssets={pack.SelectedAssetIds.Count}&selectedRelationships={pack.SelectedRelationshipIds.Count}");
}

/// <summary>Chat request from the UI.</summary>
public sealed record LandscapeChatRequest(string? Question, IReadOnlyList<string>? AssetIds);

/// <summary>Chat response back to the UI.</summary>
public sealed record LandscapeChatReply(
    string Status,
    string Message,
    string Source,
    IReadOnlyList<string> SelectedAssetIds,
    IReadOnlyList<AiDocLink> DocLinks)
{
    public static LandscapeChatReply SetupRequired(string message) =>
        new("setup-required", message, "ai:setup-required", [], []);
}

/// <summary>A stable documentation link key the API resolves into a deployment-aware URL.</summary>
public sealed record AiDocLink(string Label, string Key);
