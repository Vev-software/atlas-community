using System.Text.Json;
using Vev.Atlas.Contracts;

namespace Vev.Atlas.Domain.Portability;

/// <summary>
/// The canonical, always-present exporter: the published atlas-contracts JSON form. This is the
/// reference implementation of the seam and the format customer-owned export guarantees. Community
/// or enterprise adapters register alongside it for other formats without touching the core.
/// </summary>
public sealed class AtlasJsonLandscapeExporter : ILandscapeExporter
{
    public string Format => LandscapeFormats.AtlasJson;

    public string ContentType => "application/json";

    public string FileExtension => "json";

    public byte[] Render(LandscapeDocument landscape) =>
        JsonSerializer.SerializeToUtf8Bytes(landscape, AtlasContracts.SerializerOptions);
}

/// <summary>
/// The canonical importer: reads the published atlas-contracts JSON <see cref="ImportBundle"/>. A body
/// that is not valid JSON (or is empty) becomes a <see cref="CatalogueValidationException"/> so the
/// API answers 400, not 500.
/// </summary>
public sealed class AtlasJsonLandscapeImporter : ILandscapeImporter
{
    public string Format => LandscapeFormats.AtlasJson;

    public async ValueTask<ImportBundle> ReadAsync(Stream content, CancellationToken ct = default)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<ImportBundle>(content, AtlasContracts.SerializerOptions, ct)
                ?? throw new CatalogueValidationException("The import bundle was empty.");
        }
        catch (JsonException ex)
        {
            throw new CatalogueValidationException($"The import bundle is not valid atlas-contracts JSON: {ex.Message}");
        }
    }
}
