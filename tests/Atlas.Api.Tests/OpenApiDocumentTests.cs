using System.Net;
using System.Text.Json;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// Guards <c>GET /openapi/v1.json</c> against the ImmutableArray-default schema-generation regression
/// that OpenApiImmutableArrayDefaults fixes. The public contracts expose optional
/// <c>ImmutableArray&lt;T&gt;</c> constructor parameters (e.g. <c>ImportAsset.Tags</c>), reached through
/// the import request body; without the normalizer the schema exporter throws and the endpoint 500s.
/// The README advertises this document, so a regression here breaks the API-first onboarding path.
/// </summary>
public sealed class OpenApiDocumentTests(AtlasApiFactory factory) : IClassFixture<AtlasApiFactory>
{
    [Fact]
    public async Task OpenApi_document_is_served_and_is_valid_json()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Must parse as JSON and be a real OpenAPI document, not an error payload.
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.TryGetProperty("openapi", out _));
        Assert.True(document.RootElement.TryGetProperty("paths", out var paths));

        // The import path is the one whose request body carries the offending ImmutableArray default,
        // so its presence proves the schema for that body generated rather than aborting the document.
        Assert.True(paths.TryGetProperty("/api/v1/import", out _));
    }
}
