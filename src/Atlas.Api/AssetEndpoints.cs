using Vev.Atlas.Contracts;
using Vev.Atlas.Domain;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Api;

/// <summary>
/// The catalogue HTTP surface. Request/response bodies are the public <c>atlas-contracts</c> types —
/// the API speaks the portable contract directly (API/SDK-first, handbook 15 §2).
/// </summary>
public static class AssetEndpoints
{
    public static IEndpointRouteBuilder MapAtlasCommunityEndpoints(this IEndpointRouteBuilder app)
    {
        var assets = app.MapGroup("/api/v1/assets").WithTags("Assets");

        assets.MapGet("", async (string? kind, AssetService service, CancellationToken ct) =>
            Results.Ok(await service.ListAssetsAsync(ParseKind(kind), ct)))
            .WithName("ListAssets")
            .WithSummary("List catalogued assets, optionally filtered by kind (system|application|server|infrastructure).");

        assets.MapGet("/{id}", async (string id, AssetService service, CancellationToken ct) =>
            await service.GetAssetAsync(id, ct) is { } asset ? Results.Ok(asset) : Results.NotFound())
            .WithName("GetAsset")
            .WithSummary("Get a single asset by id.");

        assets.MapPost("", async (Asset asset, AssetService service, CancellationToken ct) =>
        {
            var created = await service.CreateAssetAsync(asset, ct);
            return Results.Created($"/api/v1/assets/{created.Id}", created);
        })
            .WithName("CreateAsset")
            .WithSummary("Create a new asset (hold it in the catalogue).");

        assets.MapPut("/{id}", async (string id, Asset asset, AssetService service, CancellationToken ct) =>
            await service.UpdateAssetAsync(id, asset, ct) is { } updated ? Results.Ok(updated) : Results.NotFound())
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
        app.MapGet("/api/v1/capabilities", (AssetService service) =>
            Results.Ok(service.DescribeCapabilities()))
            .WithTags("Session")
            .WithName("GetCapabilities")
            .WithSummary("Describe what the current principal may do in the catalogue (drives the UI's author affordances).");

        // Read-only landscape surface (atlas#6): the whole tenant map — assets + manual relationships —
        // resolved into one atlas-contracts LandscapeDocument. Backs the browse/visualise UI, which is a
        // pure client of this API (API/SDK-first — the UI is never the only way in, handbook 15 §2).
        app.MapGet("/api/v1/landscape", async (AssetService service, CancellationToken ct) =>
            Results.Ok(await service.GetLandscapeAsync(ct)))
            .WithTags("Landscape")
            .WithName("GetLandscape")
            .WithSummary("Read the whole tenant landscape (assets + relationships) as a portable LandscapeDocument.");

        var relationships = app.MapGroup("/api/v1/relationships").WithTags("Relationships");

        relationships.MapGet("", async (AssetService service, CancellationToken ct) =>
            Results.Ok(await service.ListRelationshipsAsync(ct)))
            .WithName("ListRelationships")
            .WithSummary("List manual relationships between assets.");

        relationships.MapPost("", async (Relationship relationship, AssetService service, CancellationToken ct) =>
        {
            var created = await service.CreateRelationshipAsync(relationship, ct);
            return Results.Created($"/api/v1/relationships/{created.Id}", created);
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
}
