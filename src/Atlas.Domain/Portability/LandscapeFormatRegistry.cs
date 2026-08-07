namespace Vev.Atlas.Domain.Portability;

/// <summary>
/// Resolves a portability format id to its registered adapter. This is the composition point for the
/// seam: every <see cref="ILandscapeExporter"/> / <see cref="ILandscapeImporter"/> in the container is
/// keyed by <c>Format</c>, so registering a new adapter is all a community format module has to do.
/// An unknown format is a caller error (<see cref="CatalogueValidationException"/> → 400), never a 500.
/// </summary>
public sealed class LandscapeFormatRegistry
{
    /// <summary>The format used when a caller does not name one: the canonical atlas-contracts JSON.</summary>
    public const string DefaultFormat = LandscapeFormats.AtlasJson;

    private readonly IReadOnlyDictionary<string, ILandscapeExporter> _exporters;
    private readonly IReadOnlyDictionary<string, ILandscapeImporter> _importers;

    public LandscapeFormatRegistry(IEnumerable<ILandscapeExporter> exporters, IEnumerable<ILandscapeImporter> importers)
    {
        _exporters = exporters.ToDictionary(e => e.Format, StringComparer.OrdinalIgnoreCase);
        _importers = importers.ToDictionary(i => i.Format, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The export formats currently registered (for discovery / documentation).</summary>
    public IReadOnlyCollection<string> ExportFormats => (IReadOnlyCollection<string>)_exporters.Keys;

    /// <summary>The import formats currently registered.</summary>
    public IReadOnlyCollection<string> ImportFormats => (IReadOnlyCollection<string>)_importers.Keys;

    /// <summary>Resolve the exporter for a format (or the default when none is named).</summary>
    public ILandscapeExporter ResolveExporter(string? format) =>
        Resolve(_exporters, format, "export");

    /// <summary>Resolve the importer for a format (or the default when none is named).</summary>
    public ILandscapeImporter ResolveImporter(string? format) =>
        Resolve(_importers, format, "import");

    private static T Resolve<T>(IReadOnlyDictionary<string, T> registered, string? format, string direction)
    {
        var key = string.IsNullOrWhiteSpace(format) ? DefaultFormat : format;
        if (registered.TryGetValue(key, out var adapter))
        {
            return adapter;
        }

        var known = string.Join(", ", registered.Keys.OrderBy(k => k, StringComparer.Ordinal));
        throw new CatalogueValidationException($"Unknown {direction} format '{key}'. Registered formats: {known}.");
    }
}
