using System.Collections.Immutable;
using Vev.Atlas.Contracts;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Domain;

/// <summary>
/// First-run setup copilot for Atlas Community. It is grounded strictly in the tenant's current
/// catalogue state and composes an optional Fabric AI assist with a deterministic local fallback, so the
/// product remains fully usable when no provider is configured.
/// </summary>
public sealed class SetupCopilotService(
    IRequestContextAccessor context,
    IAuthorizer authorizer,
    IAuditSink audit,
    IAssetRepository repository,
    IAiAssistService aiAssist,
    TimeProvider clock)
{
    private static readonly ResourceId SetupResource = new("atlas:setup-copilot");

    /// <summary>
    /// Describe a grounded onboarding guide for the current tenant. Read-only users may call it; it
    /// proposes only and never mutates the catalogue.
    /// </summary>
    public async Task<SetupCopilotGuide> GetGuideAsync(CancellationToken ct = default)
    {
        AuthorizeRead();

        var tenant = context.Tenant;
        var assets = await repository.ListAssetsAsync(tenant, kind: null, ct);
        var relationships = await repository.ListRelationshipsAsync(tenant, ct);
        var snapshot = BuildSnapshot(assets, relationships);
        var suggestions = BuildSuggestions(snapshot, assets);
        var features = BuildFeatures();
        var grounding = BuildGrounding(snapshot);

        var assist = aiAssist.Assist(new AiAssistRequest(
            tenant,
            context.Principal,
            AtlasCapabilities.SetupAssist,
            Purpose: "setup-onboarding",
            Grounding: grounding,
            Resource: SetupResource));

        await EmitUsageAsync(ct);

        return new SetupCopilotGuide(
            Mode: assist.Configured ? "ai" : "static",
            Source: assist.Source,
            Summary: BuildSummary(snapshot),
            Snapshot: snapshot,
            Suggestions: suggestions,
            Features: features,
            AssistantMessage: assist.Message,
            CanAuthor: authorizer.Authorize(tenant, context.Principal, AtlasActions.AssetWrite, SetupResource).Allowed);
    }

    private void AuthorizeRead()
    {
        var decision = authorizer.Authorize(context.Tenant, context.Principal, AtlasActions.AssetRead, SetupResource);
        if (!decision.Allowed)
        {
            throw AccessDeniedException.FromAuthorization(decision, $"'{AtlasActions.AssetRead}' denied ({decision.ReasonCode}).");
        }
    }

    private ValueTask EmitUsageAsync(CancellationToken ct)
    {
        // Meter/setup usage without logging prompt or customer content.
        return audit.WriteAsync(new AuditEvent(
            TenantId: context.Tenant.TenantId,
            ActorPrincipalId: context.Principal.PrincipalId,
            Action: AtlasCapabilities.SetupAssist.Value,
            Resource: SetupResource.Value,
            OccurredAt: clock.GetUtcNow(),
            CorrelationId: Guid.NewGuid().ToString("N")), ct);
    }

    private static SetupCopilotSnapshot BuildSnapshot(
        ImmutableArray<Asset> assets,
        ImmutableArray<Relationship> relationships)
    {
        var byKind = assets
            .GroupBy(a => a.Kind)
            .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal)
            .Select(g => new SetupInventoryCount(g.Key, g.Count()))
            .ToImmutableArray();

        return new SetupCopilotSnapshot(
            AssetCount: assets.Length,
            RelationshipCount: relationships.Length,
            IsEmpty: assets.IsDefaultOrEmpty,
            AssetsByKind: byKind);
    }

    private static ImmutableArray<SetupSuggestion> BuildSuggestions(
        SetupCopilotSnapshot snapshot,
        ImmutableArray<Asset> assets)
    {
        if (snapshot.IsEmpty)
        {
            return
            [
                new("Add the first system", "Start with the system or product people already talk about. That gives the rest of the catalogue a stable anchor.", "/", "new-asset"),
                new("Add one application or server", "Capture the most visible runtime first so the landscape stops being abstract and starts reflecting reality.", "/", "new-asset"),
                new("Link what belongs together", "Create a manual relationship once you have two assets so the map explains shape, not just inventory.", "/", "select-relationships")
            ];
        }

        if (snapshot.RelationshipCount == 0)
        {
            return
            [
                new("Connect the assets you already have", "The next highest-value step is usually a few \"runs on\" or \"part of\" links so the map tells a story.", "/", "select-relationships"),
                new("Fill in ownership or identifiers", "Business owner, physical name and lifecycle quickly make the catalogue useful to someone other than the author.", "/", "select-search"),
                new("Use search to verify the shape", "Search is the fastest way to spot duplicates, missing names and weak ids before the catalogue grows.", "/", "select-search")
            ];
        }

        if (!snapshot.AssetsByKind.Any(c => c.Kind is AssetKind.DataArea or AssetKind.Dataset or AssetKind.Column))
        {
            return
            [
                new("Add your first data area", "If this tenant cares about data architecture, record the first data area under a system and grow downward to datasets and columns.", "/", "new-asset"),
                new("Pin a key dataset", "A single dataset with a physical name and owner makes the data layer concrete fast.", "/", "new-asset"),
                new("Export the current baseline", "Once the first slice looks right, export the landscape JSON and keep a portable checkpoint.", "/", "export-json")
            ];
        }

        var topKind = snapshot.AssetsByKind.OrderByDescending(c => c.Count).First().Kind;
        return
        [
            new("Deepen the most populated area", $"You already have the most assets in {topKind}; add the next missing relationships or details there before broadening the scope.", "/", "select-search"),
            new("Use the map as a conversation aid", "Open a few representative assets in the detail panel and verify that names, owners and links match how the team talks about them.", "/", "select-detail"),
            new("Keep the portable export current", "The JSON export is the clean hand-off surface when you want to explain this landscape elsewhere.", "/", "export-json")
        ];
    }

    private static ImmutableArray<SetupFeature> BuildFeatures() =>
    [
        new("Landscape browse", "Inspect the tenant map and search down to dataset and column level without mutating anything.", "/", "select-search"),
        new("Guided authoring", "Authors can create assets and relationships directly from the catalogue UI; read-only users can still inspect and learn the shape.", "/", "new-asset"),
        new("Portable export", "Export the current landscape as a customer-owned JSON document when you want a checkpoint or a hand-off.", "/", "export-json")
    ];

    private static string BuildSummary(SetupCopilotSnapshot snapshot)
    {
        if (snapshot.IsEmpty)
        {
            return "This tenant is empty, so start with one system, one runtime asset, and one relationship. Atlas becomes useful once the first slice is concrete.";
        }

        if (snapshot.RelationshipCount == 0)
        {
            return $"This tenant already has {snapshot.AssetCount} asset(s), but no relationships yet. The clearest next step is to connect what belongs together so the map explains structure, not just inventory.";
        }

        return $"This tenant has {snapshot.AssetCount} asset(s) and {snapshot.RelationshipCount} relationship(s). The next value is usually deeper detail in the area you already started, not a wider but thinner inventory.";
    }

    private static string BuildGrounding(SetupCopilotSnapshot snapshot)
    {
        var byKind = snapshot.AssetsByKind.Count == 0
            ? "no assets recorded"
            : string.Join(", ", snapshot.AssetsByKind.Select(c => $"{c.Kind}:{c.Count}"));
        return $"assets={snapshot.AssetCount}; relationships={snapshot.RelationshipCount}; kinds={byKind}";
    }
}

/// <summary>A grounded onboarding guide returned by the setup copilot.</summary>
public sealed record SetupCopilotGuide(
    string Mode,
    string Source,
    string Summary,
    SetupCopilotSnapshot Snapshot,
    IReadOnlyList<SetupSuggestion> Suggestions,
    IReadOnlyList<SetupFeature> Features,
    string? AssistantMessage,
    bool CanAuthor);

/// <summary>Structured catalogue snapshot the setup guide is grounded in.</summary>
public sealed record SetupCopilotSnapshot(
    int AssetCount,
    int RelationshipCount,
    bool IsEmpty,
    IReadOnlyList<SetupInventoryCount> AssetsByKind);

/// <summary>Count of assets by kind in the grounded snapshot.</summary>
public sealed record SetupInventoryCount(AssetKind Kind, int Count);

/// <summary>A next-step suggestion for the operator.</summary>
public sealed record SetupSuggestion(string Title, string Detail, string Href, string Action);

/// <summary>A short explanation of a relevant product surface.</summary>
public sealed record SetupFeature(string Title, string Detail, string Href, string Action);
