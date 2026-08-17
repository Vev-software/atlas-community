using System.Text;
using System.Text.Json;
using Vev.Atlas.Contracts;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Domain;

/// <summary>
/// Draft structuring of user-supplied content into Atlas assets and relationships. The output is always
/// a proposal in the public atlas-contracts import shape; nothing is auto-applied.
/// </summary>
public sealed class StructureDraftService(
    IRequestContextAccessor context,
    IAuthorizer authorizer,
    IAtlasAuditSink audit,
    IAiAssistService aiAssist,
    TimeProvider clock)
{
    private static readonly ResourceId StructureResource = new("atlas:structure-draft");

    public async Task<StructureDraft> GenerateAsync(StructureDraftRequest request, CancellationToken ct = default)
    {
        AuthorizeRead();

        var text = request.Text?.Trim();
        var images = (request.Images ?? [])
            .Where(image => !string.IsNullOrWhiteSpace(image.ContentBase64))
            .ToArray();

        if (string.IsNullOrWhiteSpace(text) && images.Length == 0)
        {
            throw new CatalogueValidationException("Provide pasted text or at least one image.");
        }

        var grounding = BuildGrounding(text, images);
        var attachments = images
            .Select(image => new AiAssistAttachment(
                string.IsNullOrWhiteSpace(image.Name) ? "upload" : image.Name.Trim(),
                string.IsNullOrWhiteSpace(image.ContentType) ? "application/octet-stream" : image.ContentType.Trim(),
                image.ContentBase64.Trim()))
            .ToArray();

        var assist = aiAssist.Assist(new AiAssistRequest(
            context.Tenant,
            context.Principal,
            AtlasCapabilities.AiStructure,
            Purpose: images.Length > 0 ? "structure-multimodal" : "structure-text",
            Grounding: grounding,
            Attachments: attachments,
            Resource: StructureResource));

        var draft = assist.Configured
            ? ParseDraft(assist.Message)
            : EmptyDraft();

        await audit.WriteAsync(
            AtlasAudit.Event(context, clock, AtlasCapabilities.AiStructure.Value, AuditResource(text, images.Length, draft.Proposal.Assets.Length, draft.Proposal.Relationships.Length).Value), ct);

        if (!assist.Configured)
        {
            return draft with
            {
                Mode = StructureDraftMode.Manual,
                Status = StructureDraftStatus.AiNotConfigured,
                Source = assist.Source,
                Summary = "No AI provider is configured, so Atlas cannot propose a draft from this content yet.",
                Guidance = "Wire a Fabric AI provider, or capture the main assets and relationships manually before importing."
            };
        }

        return draft with
        {
            Mode = StructureDraftMode.Ai,
            Status = StructureDraftStatus.Available,
            Source = assist.Source,
            Summary = $"Proposed {draft.Proposal.Assets.Length} asset(s) and {draft.Proposal.Relationships.Length} relationship(s) from the supplied content."
        };
    }

    private void AuthorizeRead()
    {
        var decision = authorizer.Authorize(context.Tenant, context.Principal, AtlasActions.AssetRead, StructureResource);
        if (!decision.Allowed)
        {
            throw AccessDeniedException.FromAuthorization(decision, $"'{AtlasActions.AssetRead}' denied ({decision.ReasonCode}).");
        }
    }

    private static string BuildGrounding(string? text, IReadOnlyCollection<StructureDraftImage> images)
    {
        var summary = new StringBuilder();
        summary.AppendLine("Turn the supplied customer content into a draft atlas-contracts ImportBundle.");
        summary.AppendLine("Return JSON only. The output must stay draft-only and must not assume facts not present in the input.");
        summary.AppendLine("Use kebab-case atlas-contracts wire values for asset kinds and relationship types.");

        if (!string.IsNullOrWhiteSpace(text))
        {
            summary.AppendLine();
            summary.AppendLine("## Text input");
            summary.AppendLine(text);
        }

        if (images.Count > 0)
        {
            summary.AppendLine();
            summary.AppendLine("## Image inputs");
            foreach (var image in images)
            {
                summary.AppendLine($"- {image.Name ?? "upload"} ({image.ContentType ?? "unknown"})");
            }
        }

        return summary.ToString().TrimEnd();
    }

    private static StructureDraft ParseDraft(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new CatalogueValidationException("The AI provider returned no structure draft.");
        }

        try
        {
            var bundle = JsonSerializer.Deserialize<ImportBundle>(message, AtlasContracts.SerializerOptions)
                ?? throw new CatalogueValidationException("The AI provider returned an empty structure draft.");

            return new StructureDraft(
                Mode: StructureDraftMode.Ai,
                Status: StructureDraftStatus.Available,
                Source: "ai",
                Summary: string.Empty,
                Proposal: bundle,
                ReviewRequired: true);
        }
        catch (JsonException ex)
        {
            throw new CatalogueValidationException($"The AI provider returned invalid atlas-contracts JSON: {ex.Message}");
        }
    }

    private static StructureDraft EmptyDraft() =>
        new(
            Mode: StructureDraftMode.Manual,
            Status: StructureDraftStatus.AiNotConfigured,
            Source: "ai:unconfigured",
            Summary: string.Empty,
            Proposal: new ImportBundle(Assets: [], Relationships: [], Mode: ImportMode.Merge),
            ReviewRequired: true);

    private static ResourceId AuditResource(string? text, int imageCount, int assets, int relationships) =>
        new($"atlas:structure-draft?text={(string.IsNullOrWhiteSpace(text) ? "none" : "present")}&images={imageCount}&assets={assets}&relationships={relationships}");
}

/// <summary>Draft request for AI-assisted structuring.</summary>
public sealed record StructureDraftRequest(
    string? Text,
    IReadOnlyList<StructureDraftImage>? Images = null);

/// <summary>Image supplied for multimodal structuring.</summary>
public sealed record StructureDraftImage(
    string? Name,
    string? ContentType,
    string ContentBase64);

/// <summary>Draft import proposal returned for explicit review.</summary>
public sealed record StructureDraft(
    string Mode,
    string Status,
    string Source,
    string Summary,
    ImportBundle Proposal,
    bool ReviewRequired,
    string? Guidance = null);

/// <summary>Stable wire values for structure-draft status.</summary>
public static class StructureDraftStatus
{
    public const string Available = "available";
    public const string AiNotConfigured = "ai_not_configured";
}

/// <summary>Stable wire values for structure-draft mode.</summary>
public static class StructureDraftMode
{
    public const string Ai = "ai";
    public const string Manual = "manual";
}
