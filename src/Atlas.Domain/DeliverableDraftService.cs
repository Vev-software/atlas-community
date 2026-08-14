using System.Collections.Immutable;
using System.Text;
using Vev.Atlas.Contracts;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Domain;

/// <summary>
/// Draft deliverable generation over a bounded landscape slice. The service is grounded strictly in the
/// current tenant's catalogue and composes an optional Fabric AI assist with a deterministic local draft
/// so the workflow remains reviewable when no provider is configured.
/// </summary>
public sealed class DeliverableDraftService(
    IRequestContextAccessor context,
    IAuthorizer authorizer,
    IAuditSink audit,
    ContextPackService contextPacks,
    IAiAssistService aiAssist,
    TimeProvider clock)
{
    private static readonly ResourceId DeliverableResource = new("atlas:deliverable-draft");

    public async Task<DeliverableDraft> GenerateAsync(DeliverableDraftRequest request, CancellationToken ct = default)
    {
        AuthorizeRead();

        var format = DeliverableFormat.Normalize(request.Format);
        var pack = await contextPacks.BuildDeterministicAsync(
            request.AssetIds ?? [],
            request.RelationshipIds ?? [],
            ct);

        var selection = new DeliverableSelection(pack.Selection.AssetIds, pack.Selection.RelationshipIds);
        var title = BuildTitle(format, request.Goal, pack.Assets);
        var deterministicDraft = RenderDeterministicDraft(format, request.Goal, pack);

        var assist = aiAssist.Assist(new AiAssistRequest(
            context.Tenant,
            context.Principal,
            AtlasCapabilities.AiGenerate,
            Purpose: $"deliverable-{format}",
            Grounding: pack.Markdown,
            Resource: DeliverableResource));

        var status = assist.Configured ? DeliverableDraftStatus.Available : DeliverableDraftStatus.AiNotConfigured;
        var mode = assist.Configured ? DeliverableDraftMode.Ai : DeliverableDraftMode.Template;
        var body = assist.Configured && !string.IsNullOrWhiteSpace(assist.Message)
            ? assist.Message!
            : deterministicDraft;

        await audit.WriteAsync(new AuditEvent(
            TenantId: context.Tenant.TenantId,
            ActorPrincipalId: context.Principal.PrincipalId,
            Action: AtlasCapabilities.AiGenerate.Value,
            Resource: AuditResource(format, selection, pack.Assets.Count, pack.Relationships.Count).Value,
            OccurredAt: clock.GetUtcNow(),
            CorrelationId: Guid.NewGuid().ToString("N")), ct);

        return new DeliverableDraft(
            Format: format,
            Mode: mode,
            Status: status,
            Source: assist.Source,
            Title: title,
            Summary: pack.Summary,
            Selection: selection,
            Markdown: body,
            ReviewRequired: true);
    }

    private void AuthorizeRead()
    {
        var decision = authorizer.Authorize(context.Tenant, context.Principal, AtlasActions.AssetRead, DeliverableResource);
        if (!decision.Allowed)
        {
            throw new AccessDeniedException(decision, $"'{AtlasActions.AssetRead}' denied ({decision.ReasonCode}).");
        }
    }

    private static string BuildTitle(string format, string? goal, IReadOnlyList<Asset> assets)
    {
        if (!string.IsNullOrWhiteSpace(goal))
        {
            return goal.Trim();
        }

        var focus = assets.Count switch
        {
            0 => "landscape slice",
            1 => assets[0].Name ?? assets[0].Id,
            _ => $"{assets[0].Name ?? assets[0].Id} and {assets.Count - 1} more"
        };

        return format switch
        {
            DeliverableFormat.Deck => $"Executive deck draft for {focus}",
            DeliverableFormat.Pdf => $"PDF brief draft for {focus}",
            _ => $"Architecture brief draft for {focus}"
        };
    }

    private static string RenderDeterministicDraft(string format, string? goal, ContextPackDocument pack)
    {
        var text = new StringBuilder();
        text.AppendLine($"# {BuildTitle(format, goal, pack.Assets)}");
        text.AppendLine();
        text.AppendLine($"Draft status: {DeliverableDraftStatus.AiNotConfigured}");
        text.AppendLine();
        text.AppendLine(pack.Summary);
        text.AppendLine();
        text.AppendLine("## Review before export");
        text.AppendLine("- Confirm the selected assets and relationships still reflect the intended scope.");
        text.AppendLine("- Adjust any audience-specific wording before treating this as final.");
        text.AppendLine();

        switch (format)
        {
            case DeliverableFormat.Deck:
                text.AppendLine("## Slide outline");
                text.AppendLine("1. Why this slice matters");
                text.AppendLine("2. Current shape of the landscape");
                text.AppendLine("3. Key dependencies and risks");
                text.AppendLine("4. Decisions or follow-up");
                break;

            case DeliverableFormat.Pdf:
                text.AppendLine("## One-page structure");
                text.AppendLine("- Situation");
                text.AppendLine("- Current architecture");
                text.AppendLine("- Key dependencies");
                text.AppendLine("- Recommended next action");
                break;

            default:
                text.AppendLine("## Brief structure");
                text.AppendLine("- Purpose");
                text.AppendLine("- Grounded findings");
                text.AppendLine("- Implications");
                text.AppendLine("- Follow-up questions");
                break;
        }

        text.AppendLine();
        text.AppendLine("## Grounded assets");
        foreach (var asset in pack.Assets)
        {
            text.AppendLine($"- `{asset.Id}` ({asset.Kind})" + (string.IsNullOrWhiteSpace(asset.Name) ? string.Empty : $" — {asset.Name}"));
        }

        text.AppendLine();
        text.AppendLine("## Grounded relationships");
        if (pack.Relationships.Count == 0)
        {
            text.AppendLine("- None");
        }
        else
        {
            foreach (var relationship in pack.Relationships)
            {
                text.AppendLine($"- `{relationship.FromId}` {relationship.Type} `{relationship.ToId}`");
            }
        }

        return text.ToString().TrimEnd();
    }

    private static ResourceId AuditResource(
        string format,
        DeliverableSelection selection,
        int includedAssets,
        int includedRelationships) =>
        new($"atlas:deliverable-draft?format={format}&selectedAssets={selection.AssetIds.Count}&selectedRelationships={selection.RelationshipIds.Count}&includedAssets={includedAssets}&includedRelationships={includedRelationships}");
}

/// <summary>Input to draft generation over a selected slice.</summary>
public sealed record DeliverableDraftRequest(
    string Format,
    IReadOnlyList<string> AssetIds,
    IReadOnlyList<string>? RelationshipIds = null,
    string? Goal = null);

/// <summary>A draft deliverable returned for review before any export or publishing step.</summary>
public sealed record DeliverableDraft(
    string Format,
    string Mode,
    string Status,
    string Source,
    string Title,
    string Summary,
    DeliverableSelection Selection,
    string Markdown,
    bool ReviewRequired);

/// <summary>The bounded selection the draft is grounded in.</summary>
public sealed record DeliverableSelection(
    IReadOnlyList<string> AssetIds,
    IReadOnlyList<string> RelationshipIds);

/// <summary>Stable wire values for draft output state.</summary>
public static class DeliverableDraftStatus
{
    public const string Available = "available";
    public const string AiNotConfigured = "ai_not_configured";
}

/// <summary>Stable wire values for the draft generation mode.</summary>
public static class DeliverableDraftMode
{
    public const string Ai = "ai";
    public const string Template = "template";
}

/// <summary>Supported deliverable wire formats.</summary>
public static class DeliverableFormat
{
    public const string Deck = "deck";
    public const string Pdf = "pdf";
    public const string Doc = "doc";

    public static string Normalize(string? format)
    {
        var normalized = string.IsNullOrWhiteSpace(format) ? Deck : format.Trim().ToLowerInvariant();
        return normalized switch
        {
            Deck or Pdf or Doc => normalized,
            _ => throw new CatalogueValidationException($"Unknown deliverable format '{format}'. Expected 'deck', 'pdf' or 'doc'.")
        };
    }
}
