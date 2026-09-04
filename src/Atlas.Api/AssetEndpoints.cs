using Microsoft.AspNetCore.Mvc;
using Vev.Atlas.Contracts;
using Vev.Atlas.Domain;
using Vev.Atlas.Domain.Portability;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Api;

/// <summary>
/// The catalogue HTTP surface. Request/response bodies are the public <c>atlas-contracts</c> types —
/// the API speaks the portable contract directly (API/SDK-first, handbook 15 §2).
/// </summary>
public static class AssetEndpoints
{
    public static IEndpointRouteBuilder MapAtlasCommunityEndpoints(this IEndpointRouteBuilder app, string apiBasePath = "/api")
    {
        // The API mount path is deployment configuration (atlas#19); v1 hangs off it. Default "/api" keeps
        // the canonical "/api/v1/…" routes. Any path base (reverse-proxy sub-path) is applied separately by
        // UsePathBase, so it composes on top of these routes without being baked in here.
        var v1 = $"/{apiBasePath.Trim('/')}/v1";

        var assets = app.MapGroup($"{v1}/assets").WithTags("Assets");

        assets.MapGet("", async (string? kind, AssetService service, CancellationToken ct) =>
            Results.Ok((await service.ListCataloguedAssetsAsync(ParseKind(kind), ct)).Select(ToAssetPayload)))
            .WithName("ListAssets")
            .WithSummary("List catalogued assets, optionally filtered by kind (system|application|server|infrastructure|data-area|dataset|column).");

        assets.MapGet("/{id}", async (string id, AssetService service, CancellationToken ct) =>
            await service.GetCataloguedAssetAsync(id, ct) is { } asset ? Results.Ok(ToAssetPayload(asset)) : Results.NotFound())
            .WithName("GetAsset")
            .WithSummary("Get a single asset by id.");

        assets.MapGet("/{id}/history", async (string id, AssetService service, CancellationToken ct) =>
            await service.GetAssetHistoryAsync(id, ct) is { } history ? Results.Ok(history) : Results.NotFound())
            .WithName("GetAssetHistory")
            .WithSummary("Read the audit-backed changelog and provenance for one asset.");

        assets.MapPost("", async (Asset asset, AssetService service, CancellationToken ct) =>
        {
            var created = await service.CreateAssetAsync(asset, ct);
            return Results.Created($"{v1}/assets/{created.Asset.Id}", ToAssetPayload(created));
        })
            .WithName("CreateAsset")
            .WithSummary("Create a new asset (hold it in the catalogue).");

        assets.MapPut("/{id}", async (string id, Asset asset, AssetService service, CancellationToken ct) =>
        {
            var updated = await service.UpdateAssetAsync(id, asset, ct);
            if (updated is null)
            {
                return Results.NotFound();
            }

            var reloaded = await service.GetCataloguedAssetAsync(id, ct)
                ?? throw new InvalidOperationException($"Asset '{id}' was updated but could not be reloaded.");
            return Results.Ok(ToAssetPayload(reloaded));
        })
            .WithName("UpdateAsset")
            .WithSummary("Replace an existing asset.");

        assets.MapDelete("/{id}", async (string id, AssetService service, CancellationToken ct) =>
            await service.DeleteAssetAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteAsset")
            .WithSummary("Delete an asset.");

        // Paid-capability seam (atlas#8): the feature is not in Community, but the entitlement seam is.
        // In Community this always denies with a reason code — demonstrating the open-core line as data.
        assets.MapGet("/{id}/integration-mapping", (string id, PaidCapabilityGate gate) =>
        {
            var decision = gate.Evaluate(AtlasCapabilities.IntegrationMapping, new ResourceId($"atlas:asset/{id}"));
            if (!decision.Allowed)
            {
                return Results.Json(new
                {
                    capability = AtlasCapabilities.IntegrationMapping.Value,
                    reasonCode = decision.ReasonCode,
                    source = decision.Source,
                    upgrade = "Integration mapping is a paid Atlas capability. Contact VEV to enable it."
                }, statusCode: StatusCodes.Status402PaymentRequired);
            }

            // Not reachable in Community; the paid core implements the feature when entitled.
            return Results.Ok();
        })
            .WithName("GetAssetIntegrationMapping")
            .WithSummary("Paid capability seam: integration mapping (entitlement-gated).");

        // Session capability probe (atlas#17): tells a pure API client (the landscape UI) whether the
        // current principal may author, so it can show create/edit/delete affordances only to author-capable
        // users and keep its badge honest. The authz decision comes from Fabric, never from the UI.
        app.MapGet($"{v1}/capabilities", (AssetService service) =>
            Results.Ok(service.DescribeCapabilities()))
            .WithTags("Session")
            .WithName("GetCapabilities")
            .WithSummary("Describe what the current principal may do in the catalogue (drives the UI's author affordances).");

        // Session identity (atlas#80): returns the current principal's display name and roles so the UI can
        // show who is logged in and surface user management links. This is not an atlas-contracts portability
        // type — it describes the live session, not held data.
        app.MapGet($"{v1}/session", (IRequestContextAccessor context) =>
        {
            var principal = context.Principal;
            return Results.Ok(new
            {
                principalId = principal.PrincipalId,
                displayName = principal.DisplayName,
                roles = principal.Roles,
                tenant = context.Tenant.TenantId
            });
        })
            .WithTags("Session")
            .WithName("GetSession")
            .WithSummary("Describe the current principal's identity and roles (atlas#80).");

        // Licence & entitlements summary (atlas#147): one read-only view for the account panel — who the
        // current principal is, which edition/licence the tenant is on, which paid capabilities are enabled
        // vs. reserved, and the free AI allowance. It composes the same entitlement decisions the gates use,
        // so the client only reflects the server's decision; it never derives the free/paid line itself.
        app.MapGet($"{v1}/entitlements/summary", (EntitlementSummaryService service) =>
        {
            var summary = service.Describe();
            return Results.Ok(new
            {
                edition = summary.Edition,
                licence = new
                {
                    edition = summary.Licence.Edition,
                    state = summary.Licence.State,
                    source = summary.Licence.Source,
                    validUntil = summary.Licence.ValidUntil,
                    summary = summary.Licence.Summary
                },
                identity = new
                {
                    principalId = summary.Identity.PrincipalId,
                    displayName = summary.Identity.DisplayName,
                    roles = summary.Identity.Roles,
                    tenant = summary.Identity.Tenant
                },
                capabilities = summary.Capabilities.Select(c => new
                {
                    capability = c.Capability,
                    label = c.Label,
                    category = c.Category,
                    enabled = c.Enabled,
                    reasonCode = c.ReasonCode,
                    source = c.Source,
                    validUntil = c.ValidUntil
                }),
                aiStructure = ToAiAllowancePayload(
                    summary.AiStructure,
                    "Paste supplied notes or images into a draft landscape import bundle for review.")
            });
        })
            .WithTags("Session")
            .WithName("GetEntitlementSummary")
            .WithSummary("Describe the current tenant's edition, licence status and paid-capability entitlements (atlas#147).");

        // Extension mount surface (atlas#139): the entitled, installable ui-extensions the current tenant
        // may mount into the client's named slots — id + mount metadata only. Every registration is run
        // through the open-core install guard and the entitlement gate server-side (atlas#140/#141); a
        // denied extension is simply not listed, so nothing leaks about capabilities the tenant does not
        // hold. The client reflects this decision — it never takes it.
        app.MapGet($"{v1}/extensions/ui", async (UiExtensionCatalog catalog, CancellationToken ct) =>
            Results.Ok(new { extensions = await catalog.GetMountableAsync(ct) }))
            .WithTags("Extensions")
            .WithName("ListMountableUiExtensions")
            .WithSummary("List the ui-extensions the current tenant is entitled to mount (id + mount metadata only).");

        app.MapGet("/api/v1/setup-copilot", async (SetupCopilotService service, CancellationToken ct) =>
            Results.Ok(await service.GetGuideAsync(ct)))
            .WithTags("Session")
            .WithName("GetSetupCopilot")
            .WithSummary("Get grounded onboarding suggestions and feature explanations for the current tenant.");

        app.MapGet("/api/v1/ai/allowances", (AiAllowanceService service) =>
            Results.Ok(new
            {
                capabilities = new[]
                {
                    ToAiAllowancePayload(
                        service.Describe(AtlasCapabilities.AiStructure, new ResourceId("atlas:structure-draft")),
                        "Paste supplied notes or images into a draft landscape import bundle for review.")
                }
            }))
            .WithTags("Session")
            .WithName("GetAiAllowances")
            .WithSummary("Describe the current tenant's visible AI-hook allowances and upgrade states.");

        app.MapGet("/api/v1/ai/module", async (AiModuleService service, AtlasUrls urls, CancellationToken ct) =>
        {
            var status = await service.GetStatusAsync(ct);
            return Results.Ok(new
            {
                enabled = status.Enabled,
                consentAccepted = status.ConsentAccepted,
                provider = status.Provider,
                apiKeyConfigured = status.ApiKeyConfigured,
                ready = status.Ready,
                canManage = status.CanManage,
                consentAcceptedAt = status.ConsentAcceptedAt,
                consentAcceptedBy = status.ConsentAcceptedBy,
                allowance = ToAiAllowancePayload(status.Allowance,
                    "Paste supplied notes or images into a draft landscape import bundle for review."),
                docs = new
                {
                    setup = AtlasDocumentationLinks.Resolve(urls, "atlas-ai-setup"),
                    chat = AtlasDocumentationLinks.Resolve(urls, "atlas-ai-chat")
                }
            });
        })
            .WithTags("Session")
            .WithName("GetAiModuleStatus")
            .WithSummary("Describe the current tenant's AI module setup state without exposing the BYOK secret.");

        app.MapPut("/api/v1/ai/module", async (AiModuleSaveRequest request, AiModuleService service, AtlasUrls urls, CancellationToken ct) =>
        {
            var status = await service.SaveAsync(request, ct);
            return Results.Ok(new
            {
                enabled = status.Enabled,
                consentAccepted = status.ConsentAccepted,
                provider = status.Provider,
                apiKeyConfigured = status.ApiKeyConfigured,
                ready = status.Ready,
                canManage = status.CanManage,
                consentAcceptedAt = status.ConsentAcceptedAt,
                consentAcceptedBy = status.ConsentAcceptedBy,
                allowance = ToAiAllowancePayload(status.Allowance,
                    "Paste supplied notes or images into a draft landscape import bundle for review."),
                docs = new
                {
                    setup = AtlasDocumentationLinks.Resolve(urls, "atlas-ai-setup"),
                    chat = AtlasDocumentationLinks.Resolve(urls, "atlas-ai-chat")
                }
            });
        })
            .WithTags("Session")
            .WithName("SaveAiModuleStatus")
            .WithSummary("Enable Atlas AI for the current tenant, record consent and store the encrypted BYOK provider key.");

        app.MapDelete("/api/v1/ai/module", async (AiModuleService service, CancellationToken ct) =>
        {
            await service.DisableAsync(ct);
            return Results.NoContent();
        })
            .WithTags("Session")
            .WithName("DisableAiModule")
            .WithSummary("Disable Atlas AI and clear the stored BYOK provider key for the current tenant.");

        app.MapGet("/api/v1/ai/providers", (AiModuleService service) =>
        {
            var providers = service.GetProviderInfos();
            return Results.Ok(providers.Select(p => new
            {
                id = p.Id,
                label = p.Label,
                requiresApiKey = p.RequiresApiKey
            }));
        })
            .WithTags("Session")
            .WithName("GetAiProviders")
            .WithSummary("List all supported AI providers and whether each requires an API key.");

        app.MapPost("/api/v1/ai/chat", async (LandscapeChatRequest request, LandscapeChatService service, AtlasUrls urls, CancellationToken ct) =>
        {
            var reply = await service.AskAsync(request, ct);
            return Results.Ok(new
            {
                status = reply.Status,
                message = reply.Message,
                source = reply.Source,
                selectedAssetIds = reply.SelectedAssetIds,
                docs = reply.DocLinks.Select(link => new
                {
                    label = link.Label,
                    href = AtlasDocumentationLinks.Resolve(urls, link.Key)
                })
            });
        })
            .WithTags("Session")
            .WithName("AskLandscapeChat")
            .WithSummary("Ask a grounded, read-only question about the current tenant landscape.");

        // Read-only landscape surface (atlas#6): the whole tenant map — assets + manual relationships —
        // resolved into one atlas-contracts LandscapeDocument. Backs the browse/visualise UI, which is a
        // pure client of this API (API/SDK-first — the UI is never the only way in, handbook 15 §2).
        app.MapGet($"{v1}/landscape", async (AssetService service, CancellationToken ct) =>
        {
            var landscape = await service.GetLandscapeAsync(ct);
            var assetsWithNumericIds = await service.ListCataloguedAssetsAsync(kind: null, ct);
            return Results.Ok(ToLandscapePayload(landscape, assetsWithNumericIds));
        })
            .WithTags("Landscape")
            .WithName("GetLandscape")
            .WithSummary("Read the whole tenant landscape (assets + relationships) as a portable LandscapeDocument.");

        // Shadow-IT visibility (issue #25): computed signals over the existing catalogue — no separate model.
        // Unowned, unsanctioned, and past-EOL assets are surfaced as a Community quick win that drives adoption.
        var shadowIt = app.MapGroup($"{v1}/shadow-it").WithTags("Shadow-IT");

        shadowIt.MapGet("/summary", async (ShadowItService service, CancellationToken ct) =>
        {
            var summary = await service.GetSummaryAsync(ct);
            return Results.Ok(summary);
        })
            .WithName("GetShadowItSummary")
            .WithSummary("Compute ownership coverage and shadow-IT signal counts for the current tenant.");

        shadowIt.MapGet("/assets", async (
            bool? unowned,
            bool? unsanctioned,
            bool? pastEol,
            ShadowItService service,
            CancellationToken ct) =>
        {
            var filter = new ShadowItFilter(
                IncludeUnowned: unowned,
                IncludeUnsanctioned: unsanctioned,
                IncludePastEol: pastEol);
            var flagged = await service.GetFlaggedAssetsAsync(filter, ct);
            return Results.Ok(flagged);
        })
            .WithName("GetShadowItAssets")
            .WithSummary("Filter assets by shadow-IT signals (unowned, unsanctioned, past-EOL). Flags use OR logic.");

        // Portability surface (issue #12): customer-owned export + import in the published contract form.
        // Both endpoints route through the format-adapter seam (LandscapeFormatRegistry), so a future
        // community adapter (ArchiMate/BPMN/report) is added by registering an ILandscapeExporter/Importer,
        // never by touching the core boundary here.
        var portability = app.MapGroup(v1).WithTags("Portability");

        portability.MapGet("/context-pack", async (
            [FromQuery(Name = "assetId")] string[] assetIds,
            [FromQuery(Name = "relationshipId")] string[] relationshipIds,
            string? mode,
            ContextPackService service,
            PaidCapabilityGate gate,
            CancellationToken ct) =>
        {
            var requestedMode = string.IsNullOrWhiteSpace(mode) ? ContextPackMode.Deterministic : mode;
            if (string.Equals(requestedMode, ContextPackMode.Narrative, StringComparison.OrdinalIgnoreCase))
            {
                var decision = gate.Evaluate(AtlasCapabilities.AiBrief, new ResourceId("atlas:context-pack"));
                if (!decision.Allowed)
                {
                    return Results.Json(new
                    {
                        capability = AtlasCapabilities.AiBrief.Value,
                        reasonCode = decision.ReasonCode,
                        source = decision.Source,
                        upgrade = "Narrative context briefs are a paid Atlas capability. Contact VEV to enable them."
                    }, statusCode: StatusCodes.Status402PaymentRequired);
                }
            }

            return Results.Ok(await service.ExportAsync(assetIds, relationshipIds, requestedMode, ct));
        })
            .WithName("ExportContextPack")
            .WithSummary("Export a bounded landscape slice as a grounded context pack for external AI or human hand-off.");

        portability.MapPost("/structure/draft", async (
            StructureDraftRequest request,
            StructureDraftService service,
            AiAllowanceService allowances,
            CancellationToken ct) =>
        {
            var allowance = allowances.Describe(AtlasCapabilities.AiStructure, new ResourceId("atlas:structure-draft"));
            if (!allowance.Allowed)
            {
                return Results.Json(
                    ToAiAllowancePayload(
                        allowance,
                        "Paste supplied notes or images into a draft landscape import bundle for review."),
                    statusCode: StatusCodes.Status402PaymentRequired);
            }

            return Results.Ok(await service.GenerateAsync(request, ct));
        })
            .WithName("GenerateStructureDraft")
            .WithSummary("Turn pasted text or uploaded images into a draft atlas-contracts import proposal for review.");

        portability.MapPost("/deliverables/draft", async (
            DeliverableDraftRequest request,
            DeliverableDraftService service,
            PaidCapabilityGate gate,
            CancellationToken ct) =>
        {
            var decision = gate.Evaluate(AtlasCapabilities.AiGenerate, new ResourceId("atlas:deliverable-draft"));
            if (!decision.Allowed)
            {
                return Results.Json(new
                {
                    capability = AtlasCapabilities.AiGenerate.Value,
                    reasonCode = decision.ReasonCode,
                    source = decision.Source,
                    upgrade = "AI-generated deliverables are a paid Atlas capability. Contact VEV to enable them."
                }, statusCode: StatusCodes.Status402PaymentRequired);
            }

            return Results.Ok(await service.GenerateAsync(request, ct));
        })
            .WithName("GenerateDeliverableDraft")
            .WithSummary("Generate a grounded draft deliverable for review from a selected landscape slice.");

        portability.MapGet("/export", async (string? format, AssetService service, LandscapeFormatRegistry formats, CancellationToken ct) =>
        {
            var exporter = formats.ResolveExporter(format);
            // A full-map export is authorized (elevated role) and audited exactly once in the domain — a
            // read-only customer is denied (403), and no export is a silent bulk read (atlas#36).
            var landscape = await service.ExportLandscapeAsync(exporter.Format, ct);
            var bytes = exporter.Render(landscape);
            // Content-Disposition: attachment — the customer's landscape as a portable file they own.
            return Results.File(bytes, exporter.ContentType, $"atlas-landscape.{exporter.FileExtension}");
        })
            .WithName("ExportLandscape")
            .WithSummary("Export the tenant landscape as a downloadable atlas-contracts document (customer-owned export).")
            // Throttle so the whole landscape cannot be pulled in a tight loop (atlas#36).
            .RequireRateLimiting(ExportRateLimit.PolicyName);

        portability.MapPost("/import", async (string? format, HttpRequest request, AssetService service, LandscapeFormatRegistry formats, CancellationToken ct) =>
        {
            var importer = formats.ResolveImporter(format);
            var bundle = await importer.ReadAsync(request.Body, ct);
            var result = await service.ImportLandscapeAsync(bundle, ct);
            return Results.Ok(result);
        })
            .WithName("ImportLandscape")
            .WithSummary("Import a portable atlas-contracts bundle (Merge upserts; Replace matches the target to the bundle).");

        var relationships = app.MapGroup($"{v1}/relationships").WithTags("Relationships");

        relationships.MapGet("", async (AssetService service, CancellationToken ct) =>
            Results.Ok(await service.ListRelationshipsAsync(ct)))
            .WithName("ListRelationships")
            .WithSummary("List manual relationships between assets.");

        relationships.MapPost("", async (Relationship relationship, AssetService service, CancellationToken ct) =>
        {
            var created = await service.CreateRelationshipAsync(relationship, ct);
            return Results.Created($"{v1}/relationships/{created.Id}", created);
        })
            .WithName("CreateRelationship")
            .WithSummary("Create a manual relationship between two existing assets.");

        relationships.MapDelete("/{id}", async (string id, AssetService service, CancellationToken ct) =>
            await service.DeleteRelationshipAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteRelationship")
            .WithSummary("Delete a manual relationship.");

        return app;
    }

    // Parse the kind filter using the contract's own wire vocabulary (lowercase, e.g. "server"),
    // so the API speaks exactly what atlas-contracts publishes. An unknown value is a 400.
    private static AssetKind? ParseKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<AssetKind>($"\"{kind}\"", AtlasContracts.SerializerOptions);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new CatalogueValidationException($"Unknown asset kind '{kind}'.");
        }
    }

    private static object ToAiAllowancePayload(AiAllowanceSnapshot allowance, string hook) => new
    {
        capability = allowance.Capability,
        status = allowance.Status,
        reasonCode = allowance.ReasonCode,
        source = allowance.Source,
        limit = allowance.Limit,
        used = allowance.Used,
        remaining = allowance.Remaining,
        window = allowance.Window,
        unlimited = allowance.Unlimited,
        hook,
        upgrade = BuildAllowanceUpgradeMessage(allowance)
    };

    private static string BuildAllowanceUpgradeMessage(AiAllowanceSnapshot allowance) =>
        allowance.Status switch
        {
            AiAllowanceStatus.Exhausted when allowance.Limit is { } limit =>
                $"You have used {allowance.Used} of {limit} free AI structurings today. Upgrade to Atlas Enterprise for a higher or unlimited allowance.",
            AiAllowanceStatus.Limited when allowance.Limit is { } limit =>
                $"You have used {allowance.Used} of {limit} free AI structurings today.",
            AiAllowanceStatus.Unlimited =>
                "This tenant has an entitled AI allowance for landscape structuring.",
            _ =>
                "AI-assisted landscape structuring is not enabled for this tenant."
        };

    private static object ToAssetPayload(CataloguedAsset asset) => new
    {
        id = asset.Asset.Id,
        numericId = asset.NumericId,
        kind = asset.Asset.Kind,
        name = asset.Asset.Name,
        lifecycle = asset.Asset.Lifecycle,
        description = asset.Asset.Description,
        tags = asset.Asset.Tags,
        createdBy = asset.CreatedBy,
        application = asset.Asset.Application,
        server = asset.Asset.Server,
        infrastructure = asset.Asset.Infrastructure,
        dataArea = asset.Asset.DataArea,
        dataset = asset.Asset.Dataset,
        column = asset.Asset.Column
    };

    private static object ToLandscapePayload(LandscapeDocument landscape, IEnumerable<CataloguedAsset> assets) => new
    {
        contractVersion = landscape.ContractVersion,
        exportedAt = landscape.ExportedAt,
        generator = landscape.Generator,
        assets = assets.Select(ToAssetPayload),
        relationships = landscape.Relationships
    };
}
