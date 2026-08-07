using Vev.Atlas.Contracts;

namespace Vev.Atlas.Domain.Portability;

/// <summary>
/// The portability format-adapter seam. The <b>core boundary</b> is the canonical atlas-contracts
/// form — <see cref="LandscapeDocument"/> out, <see cref="ImportBundle"/> in. A format adapter's only
/// job is to translate <i>between</i> some external format and that canonical form; the tenant-scoped,
/// authorized, audited apply logic in <see cref="AssetService"/> never changes. This is the explicit
/// seam future community adapters (ArchiMate, BPMN, report) compose onto: implement
/// <see cref="ILandscapeExporter"/> / <see cref="ILandscapeImporter"/>, register it, done
/// (issue #12; handbook 11 §2-3, 12 §Phase C).
/// </summary>
public interface ILandscapeExporter
{
    /// <summary>The format this exporter produces. Lowercase, kebab-case (e.g. <c>atlas-json</c>).</summary>
    string Format { get; }

    /// <summary>The MIME type of the rendered artifact, for the HTTP <c>Content-Type</c>.</summary>
    string ContentType { get; }

    /// <summary>The file extension (no dot) for the downloadable artifact, e.g. <c>json</c>.</summary>
    string FileExtension { get; }

    /// <summary>Render a canonical landscape document into portable bytes in this format.</summary>
    byte[] Render(LandscapeDocument landscape);
}

/// <summary>
/// The import half of the format-adapter seam: parse portable input in some format back into the
/// canonical <see cref="ImportBundle"/>, which the core then validates and applies. Malformed input
/// should surface as a <see cref="CatalogueValidationException"/> (a 400), never an unhandled 500.
/// </summary>
public interface ILandscapeImporter
{
    /// <summary>The format this importer consumes. Lowercase, kebab-case (e.g. <c>atlas-json</c>).</summary>
    string Format { get; }

    /// <summary>Parse portable content into a canonical import bundle.</summary>
    ValueTask<ImportBundle> ReadAsync(Stream content, CancellationToken ct = default);
}

/// <summary>Well-known portability format identifiers.</summary>
public static class LandscapeFormats
{
    /// <summary>The canonical atlas-contracts JSON form (the published, versioned contract).</summary>
    public const string AtlasJson = "atlas-json";
}
