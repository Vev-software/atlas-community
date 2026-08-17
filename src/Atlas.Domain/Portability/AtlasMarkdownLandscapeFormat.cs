using System.Collections.Immutable;
using Vev.Atlas.Contracts;

namespace Vev.Atlas.Domain.Portability;

/// <summary>
/// Parses the documented, human-writable Markdown landscape format into the canonical
/// <see cref="ImportBundle"/>. Format: H2 sections per asset kind, pipe-delimited asset lines,
/// key:value detail lines, and a Relationships section. Malformed input throws
/// <see cref="CatalogueValidationException"/> (400), never 500.
/// </summary>
public sealed class AtlasMarkdownLandscapeImporter : ILandscapeImporter
{
    public string Format => LandscapeFormats.AtlasMarkdown;

    public async ValueTask<ImportBundle> ReadAsync(Stream content, CancellationToken ct = default)
    {
        using var reader = new StreamReader(content, leaveOpen: true);
        var text = await reader.ReadToEndAsync(ct);
        return Parse(text);
    }

    private static ImportBundle Parse(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var assets = new List<ImportAsset>();
        var relationships = new List<ImportRelationship>();
        var mode = ImportMode.Merge;

        var currentKind = AssetKind.System;
        var currentSection = Section.Assets;
        var currentAsset = new AssetBuilder(null!, AssetKind.System, null!, Lifecycle.Draft);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            if (IsSectionHeader(line, "Systems")) { currentKind = AssetKind.System; currentSection = Section.Assets; continue; }
            if (IsSectionHeader(line, "Applications")) { currentKind = AssetKind.Application; currentSection = Section.Assets; continue; }
            if (IsSectionHeader(line, "Servers")) { currentKind = AssetKind.Server; currentSection = Section.Assets; continue; }
            if (IsSectionHeader(line, "Infrastructure")) { currentKind = AssetKind.Infrastructure; currentSection = Section.Assets; continue; }
            if (IsSectionHeader(line, "Data Areas")) { currentKind = AssetKind.DataArea; currentSection = Section.Assets; continue; }
            if (IsSectionHeader(line, "Datasets")) { currentKind = AssetKind.Dataset; currentSection = Section.Assets; continue; }
            if (IsSectionHeader(line, "Columns")) { currentKind = AssetKind.Column; currentSection = Section.Assets; continue; }
            if (line.Equals("## Relationships", StringComparison.Ordinal)) { currentSection = Section.Relationships; continue; }
            if (line.Equals("## Mode", StringComparison.Ordinal)) { currentSection = Section.Mode; continue; }

            if (currentSection == Section.Mode)
            {
                if (line.Equals("merge", StringComparison.OrdinalIgnoreCase)) mode = ImportMode.Merge;
                else if (line.Equals("replace", StringComparison.OrdinalIgnoreCase)) mode = ImportMode.Replace;
                else throw new CatalogueValidationException($"Unknown import mode '{line}'. Use 'merge' or 'replace'.");
                continue;
            }

            if (currentSection == Section.Relationships)
            {
                if (line.StartsWith("-"))
                {
                    var rel = ParseRelationship(line.Substring(1).Trim());
                    relationships.Add(rel);
                }
                continue;
            }

            if (currentSection == Section.Assets)
            {
                if (line.StartsWith("-"))
                {
                    if (currentAsset.Id != null)
                        assets.Add(currentAsset.Build());
                    currentAsset = ParseAssetLine(line.Substring(1).Trim(), currentKind);
                }
                else if (line.Contains(':') && currentAsset.Id != null)
                {
                    var colonIdx = line.IndexOf(':');
                    var key = line.Substring(0, colonIdx).Trim();
                    var value = line.Substring(colonIdx + 1).Trim();
                    currentAsset.Set(key, value);
                }
            }
        }

        if (currentAsset.Id != null)
            assets.Add(currentAsset.Build());

        return new ImportBundle(
            Assets: assets.ToImmutableArray(),
            Relationships: relationships.ToImmutableArray(),
            Mode: mode);
    }

    private static bool IsSectionHeader(string line, string name) =>
        line.Equals($"## {name}", StringComparison.Ordinal) ||
        line.Equals($"### {name}", StringComparison.Ordinal);

    private static ImportRelationship ParseRelationship(string line)
    {
        var parts = line.Split([' ', '\t'], 4, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
            throw new CatalogueValidationException($"Malformed relationship line: '{line}'. Expected 'fromRef type toRef [description]'.");

        var fromRef = parts[0];
        var relType = ParseRelationshipType(parts[1]);
        var toRef = parts[2];
        string? description = parts.Length >= 4 ? parts[3] : null;

        return new ImportRelationship(fromRef, toRef, relType, description);
    }

    private static RelationshipType ParseRelationshipType(string value) => value switch
    {
        "runs-on" or "runson" => RelationshipType.RunsOn,
        "hosts" => RelationshipType.Hosts,
        "connects-to" or "connectsto" => RelationshipType.ConnectsTo,
        "depends-on" or "dependson" => RelationshipType.DependsOn,
        "part-of" or "partof" => RelationshipType.PartOf,
        "joins-on" or "joinson" => RelationshipType.JoinsOn,
        _ => throw new CatalogueValidationException($"Unknown relationship type '{value}'. Use runs-on, hosts, connects-to, depends-on, part-of, or joins-on.")
    };

    private static AssetBuilder ParseAssetLine(string line, AssetKind kind)
    {
        var fields = SplitPipe(line);
        if (fields.Length < 3)
            throw new CatalogueValidationException($"Malformed asset line: '{line}'. Expected '- id | name | lifecycle'.");

        var id = fields[0].Trim();
        var name = fields[1].Trim();
        var lifecycleStr = fields[2].Trim();

        if (string.IsNullOrEmpty(id))
            throw new CatalogueValidationException("Asset id must not be empty.");
        if (string.IsNullOrEmpty(name))
            throw new CatalogueValidationException("Asset name must not be empty.");

        var lifecycle = ParseLifecycle(lifecycleStr);
        return new AssetBuilder(id, kind, name, lifecycle);
    }

    private static Lifecycle ParseLifecycle(string value) => value.ToLowerInvariant() switch
    {
        "draft" => Lifecycle.Draft,
        "active" => Lifecycle.Active,
        "retired" => Lifecycle.Retired,
        _ => throw new CatalogueValidationException($"Unknown lifecycle '{value}'. Use 'draft', 'active', or 'retired'.")
    };

    private static string[] SplitPipe(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var c in line)
        {
            if (c == '|')
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result.ToArray();
    }

    private enum Section { Assets, Relationships, Mode }

    private sealed class AssetBuilder
    {
        public string Id { get; }
        private readonly AssetKind _kind;
        private readonly string _name;
        private readonly Lifecycle _lifecycle;
        private string? _description;
        private readonly List<Tag> _tags = [];
        private readonly Dictionary<string, string> _details = [];

        public AssetBuilder(string id, AssetKind kind, string name, Lifecycle lifecycle)
        {
            Id = id;
            _kind = kind;
            _name = name;
            _lifecycle = lifecycle;
        }

        public void Set(string key, string value)
        {
            var k = key.ToLowerInvariant();
            if (k == "description" || k == "desc")
            {
                _description = value;
                return;
            }

            if (k == "tag" || k == "tags")
            {
                var tv = value.Split(':', 2);
                var tagKey = tv[0].Trim();
                var tagValue = tv.Length > 1 ? tv[1].Trim() : null;
                _tags.Add(tagValue is { } v ? new Tag(tagKey, v) : new Tag(tagKey));
                return;
            }

            _details[k] = value;
        }

        public ImportAsset Build()
        {
            ApplicationDetails? app = null;
            ServerDetails? srv = null;
            InfrastructureDetails? inf = null;
            DataAreaDetails? da = null;
            DatasetDetails? ds = null;
            ColumnDetails? col = null;

            switch (_kind)
            {
                case AssetKind.Application:
                    app = new ApplicationDetails(
                        Version: _details.GetValueOrDefault("version") ?? _details.GetValueOrDefault("ver"),
                        Vendor: _details.GetValueOrDefault("vendor"),
                        BusinessOwner: _details.GetValueOrDefault("businessowner") ?? _details.GetValueOrDefault("business_owner") ?? _details.GetValueOrDefault("owner"));
                    break;
                case AssetKind.Server:
                    srv = new ServerDetails(
                        Hostname: _details.GetValueOrDefault("hostname") ?? _details.GetValueOrDefault("host"),
                        Environment: _details.GetValueOrDefault("environment") ?? _details.GetValueOrDefault("env"),
                        OperatingSystem: _details.GetValueOrDefault("operatingsystem") ?? _details.GetValueOrDefault("os"));
                    break;
                case AssetKind.Infrastructure:
                    inf = new InfrastructureDetails(
                        Category: _details.GetValueOrDefault("category") ?? _details.GetValueOrDefault("cat"),
                        Location: _details.GetValueOrDefault("location") ?? _details.GetValueOrDefault("loc"));
                    break;
                case AssetKind.DataArea:
                    var realisation = _details.GetValueOrDefault("realisation");
                    if (realisation != null)
                        da = new DataAreaDetails(realisation);
                    break;
                case AssetKind.Dataset:
                    ds = new DatasetDetails(
                        PhysicalName: _details.GetValueOrDefault("physicalname") ?? _details.GetValueOrDefault("physical_name") ?? _details.GetValueOrDefault("table"),
                        Owner: _details.GetValueOrDefault("owner"));
                    break;
                case AssetKind.Column:
                    var dataType = _details.GetValueOrDefault("datatype") ?? _details.GetValueOrDefault("data_type") ?? _details.GetValueOrDefault("type");
                    bool? nullable = null;
                    if (_details.TryGetValue("nullable", out var nullStr) || _details.TryGetValue("null", out nullStr))
                        nullable = bool.Parse(nullStr);
                    col = new ColumnDetails(dataType, nullable);
                    break;
            }

            return new ImportAsset(
                Kind: _kind,
                Name: _name,
                Lifecycle: _lifecycle,
                Id: Id,
                Description: _description,
                Tags: [.. _tags],
                Application: app,
                Server: srv,
                Infrastructure: inf,
                DataArea: da,
                Dataset: ds,
                Column: col);
        }
    }
}
