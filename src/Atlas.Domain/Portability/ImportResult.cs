using System.Text.Json.Serialization;
using Vev.Atlas.Contracts;

namespace Vev.Atlas.Domain.Portability;

/// <summary>
/// The outcome of applying an <see cref="ImportBundle"/>: how it was applied and what changed. Held
/// counts, not analysis — enough for a caller (or CI) to assert an import did what it claimed.
/// </summary>
/// <param name="Mode">How the bundle was applied (merge upserts; replace matches target to the bundle).</param>
/// <param name="AssetsCreated">Assets that did not exist and were inserted.</param>
/// <param name="AssetsUpdated">Assets that already existed and were replaced.</param>
/// <param name="AssetsDeleted">Assets removed because they were absent from the bundle (Replace only).</param>
/// <param name="RelationshipsImported">Manual relationships upserted from the bundle.</param>
public sealed record ImportResult(
    [property: JsonPropertyName("mode")] ImportMode Mode,
    [property: JsonPropertyName("assetsCreated")] int AssetsCreated,
    [property: JsonPropertyName("assetsUpdated")] int AssetsUpdated,
    [property: JsonPropertyName("assetsDeleted")] int AssetsDeleted,
    [property: JsonPropertyName("relationshipsImported")] int RelationshipsImported);
