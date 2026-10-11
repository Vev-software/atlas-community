using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Vev.Atlas.Contracts;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Domain.Sharing;

/// <summary>The detached signature of a digest (RSA PKCS#1 v1.5 over SHA-256 of the RFC 8785 canonical digest).</summary>
public sealed record DigestSignature(
    [property: JsonPropertyName("algorithm")] string Algorithm,
    [property: JsonPropertyName("keyId")] string KeyId,
    [property: JsonPropertyName("publicKey")] string PublicKey,
    [property: JsonPropertyName("value")] string Value);

/// <summary>The delivery wrapper of a signed digest, as documented with the digest contract.</summary>
public sealed record SignedLandscapeDigest(
    [property: JsonPropertyName("digest")] object Digest,
    [property: JsonPropertyName("signature")] DigestSignature Signature)
{
    /// <summary>Contract version of the digest inside: 1, or 2 when the sharer opted in to relations.</summary>
    [JsonIgnore]
    public string ContractVersion => Digest is LandscapeDigestV2 ? "2" : "1";

    [JsonIgnore]
    public string DigestId => Digest is LandscapeDigestV2 v2 ? v2.DigestId : ((LandscapeDigest)Digest).DigestId;

    [JsonIgnore]
    public int Sequence => Digest is LandscapeDigestV2 v2 ? v2.Sequence : ((LandscapeDigest)Digest).Sequence;

    [JsonIgnore]
    public int ItemCount => Digest is LandscapeDigestV2 v2 ? v2.Items.Length : ((LandscapeDigest)Digest).Items.Length;

    /// <summary>Edge counts by kind (zero for a v1 digest). Counts only, safe for an audit record.</summary>
    [JsonIgnore]
    public (int RunsOn, int SuppliedBy, int Integrates) RelationCounts => Digest is LandscapeDigestV2 { Relations: { } edges }
        ? (edges.Count(e => e.Kind == DigestRelationKind.RunsOn), edges.Count(e => e.Kind == DigestRelationKind.SuppliedBy), edges.Count(e => e.Kind == DigestRelationKind.Integrates))
        : (0, 0, 0);
}

/// <summary>
/// What relation sharing the sharer opted in to. <see cref="Relations"/> is stage 1 (runsOn, suppliedBy, layer); <see cref="Integrations"/> is the separate
/// stage 2 consent (undirected integrates edges) and can only be on together with relations. Both are consent, never authorization.
/// </summary>
public readonly record struct RelationOptions(bool Relations = false, bool Integrations = false)
{
    public static readonly RelationOptions None = default;

    /// <summary>Stage 2 needs stage 1: an integrations opt-in without relations is refused, not ignored.</summary>
    public RelationOptions Validated() => Integrations && !Relations
        ? throw new CatalogueValidationException("Sharing integrations between systems needs 'Share relations' to be on as well.")
        : this;
}

/// <summary>One edge as the sharer sees it before sharing: item names and the closed kind, nothing else.</summary>
public sealed record PreviewRelation(string From, string To, string Kind);

/// <summary>
/// What a digest would hold, shown before anything leaves the installation. <see cref="Shared"/> and
/// <see cref="NeverShared"/> state the field-level rule so the sharer sees what is, and what can never be, in the file.
/// </summary>
public sealed record DigestPreview(
    LandscapeDigestScope Scope,
    ImmutableArray<LandscapeDigestItem> Items,
    ImmutableArray<string> Shared,
    ImmutableArray<string> NeverShared,
    bool RelationsIncluded = false,
    ImmutableArray<PreviewRelation>? Relations = null,
    int RunsOnCount = 0,
    int SuppliedByCount = 0,
    int IntegratesCount = 0);

/// <summary>Signs a digest with the installation's key. The key itself never leaves the implementation.</summary>
public interface IDigestSigner
{
    Task<DigestSignature> SignAsync(byte[] canonicalDigest, CancellationToken ct = default);
}

/// <summary>
/// The per-tenant source identity and its monotonic sequence (replay protection). <see cref="NextAsync"/> reserves the
/// next sequence, so two digests never share one. The source id is opaque and carries no tenant or customer meaning.
/// </summary>
public interface IDigestStateStore
{
    Task<(string SourceInstanceId, int Sequence)> NextAsync(TenantContext tenant, CancellationToken ct = default);
}

/// <summary>
/// Share a minimized landscape summary as a signed file (atlas#175). A digest is an elevated, recorded action like
/// the portability export: it needs <see cref="AtlasActions.LandscapeShare"/> (a read-only customer is denied), it
/// writes exactly one audit record, and it is built by <see cref="LandscapeDigestBuilder"/>, which reads only what
/// the contract allows. A preview shows the same items without a sequence number or an audit record, since nothing
/// leaves the installation until the file is downloaded.
/// </summary>
public sealed class LandscapeShareService(
    IRequestContextAccessor context,
    IAuthorizer authorizer,
    IAtlasAuditSink audit,
    IAssetRepository repository,
    IDigestSigner signer,
    IDigestStateStore state,
    PaidCapabilityGate gate,
    TimeProvider clock)
{
    public const string SignatureAlgorithm = "rsa-pkcs1-v1_5-sha256";

    private static readonly ResourceId LandscapeResource = new("atlas:landscape");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly ImmutableArray<string> SharedFields =
        ["kind", "name", "lifecycle", "vendor or provider (applications and AI services or models)", "number of connects-to relationships (systems and applications)"];

    /// <summary>What an edge reveals; shown next to the "Share relations" switch and added to the preview when it is on.</summary>
    public static readonly ImmutableArray<string> RelationFields =
        ["which system or application runs on which platform", "which vendor supplies which component", "a layer hint (application or infrastructure) per item"];

    /// <summary>What the separate stage 2 switch adds: which systems talk to each other, and nothing about how.</summary>
    public static readonly ImmutableArray<string> IntegrationFields =
        ["which systems and applications exchange data with each other (an undirected pair, with no direction, label, protocol or data description)"];

    public static readonly ImmutableArray<string> NeverSharedFields =
        ["descriptions", "hostnames and IP addresses", "servers' environment and operating system", "endpoints and locations", "owners and people", "attachments",
         "data areas, datasets and columns", "relationship details", "credentials"];

    public async Task<DigestPreview> PreviewAsync(LandscapeDigestScope scope, RelationOptions relations = default, CancellationToken ct = default)
    {
        Authorize();
        relations.Validated();
        if (relations.Relations) RequireRelations();
        var (assets, relationships) = await ReadAsync(ct);
        var items = LandscapeDigestBuilder.BuildItems(assets, relationships, scope);
        if (!relations.Relations) return new DigestPreview(scope, items, SharedFields, NeverSharedFields);

        var v2 = LandscapeDigestBuilder.BuildV2(assets, relationships, scope, "preview", "preview", 0, clock.GetUtcNow(), relations.Integrations);
        var nameOf = v2.Items.ToDictionary(i => i.Ref, i => i.Name, StringComparer.Ordinal);
        var edges = v2.Relations ?? [];
        return new DigestPreview(scope, items, [.. SharedFields, .. RelationFields, .. relations.Integrations ? IntegrationFields : []], NeverSharedFields, RelationsIncluded: true,
            Relations: [.. edges.Select(e => new PreviewRelation(nameOf[e.From], nameOf[e.To], Wire(e.Kind)))],
            RunsOnCount: edges.Count(e => e.Kind == DigestRelationKind.RunsOn), SuppliedByCount: edges.Count(e => e.Kind == DigestRelationKind.SuppliedBy),
            IntegratesCount: edges.Count(e => e.Kind == DigestRelationKind.Integrates));
    }

    public async Task<SignedLandscapeDigest> CreateAsync(LandscapeDigestScope scope, RelationOptions relations = default, CancellationToken ct = default)
    {
        Authorize();
        relations.Validated();
        if (relations.Relations) RequireRelations();
        var signed = await BuildSignedAsync(scope, relations, ct);

        // Counts and kinds only: no item name or customer content in the audit trail. It records who shared what scope and whether relations were part of it.
        await audit.WriteAsync(AtlasAudit.Event(context, clock, "atlas.landscape.shared",
            $"atlas:landscape/share?digest={signed.DigestId}&kinds={string.Join(',', scope.Kinds.Select(Wire))}&tags={scope.Tags.Length}&items={signed.ItemCount}&sequence={signed.Sequence}{RelationAudit(signed)}"), ct);
        return signed;
    }

    /// <summary>The audit suffix for relations: whether they were included and the edge counts by kind.</summary>
    public static string RelationAudit(SignedLandscapeDigest signed) =>
        signed.ContractVersion == "2" ? $"&relations=true&runsOn={signed.RelationCounts.RunsOn}&suppliedBy={signed.RelationCounts.SuppliedBy}&integrates={signed.RelationCounts.Integrates}" : "&relations=false";

    /// <summary>Whether this tenant holds the separate relations grant. A push with relations on falls back to v1 while it does not.</summary>
    public bool CanShareRelations() => gate.Evaluate(AtlasCapabilities.LandscapeShareRelations, LandscapeResource).Allowed;

    /// <summary>
    /// A signed digest for an outbound push (atlas#176). It is authorized like the file and uses a sequence number, but it does
    /// not write the file's audit record: the push writes its own, exactly one per push.
    /// </summary>
    public async Task<SignedLandscapeDigest> CreateForPushAsync(LandscapeDigestScope scope, RelationOptions relations = default, CancellationToken ct = default)
    {
        Authorize();
        relations.Validated();
        if (relations.Relations) RequireRelations();
        return await BuildSignedAsync(scope, relations, ct);
    }

    /// <summary>
    /// A fingerprint of what a digest with this scope would hold now, without a sequence number. Equal fingerprints mean nothing
    /// the consumer can see has changed, so an on-change push can be skipped.
    /// </summary>
    public async Task<string> ContentFingerprintAsync(LandscapeDigestScope scope, RelationOptions relations = default, CancellationToken ct = default)
    {
        Authorize();
        relations.Validated();
        if (relations.Relations) RequireRelations();
        var (assets, relationships) = await ReadAsync(ct);
        var items = LandscapeDigestBuilder.BuildItems(assets, relationships, scope);
        // With relations on the edges and layers are part of what the consumer can see, so a change to them is a change to push.
        var edges = relations.Relations ? LandscapeDigestBuilder.BuildV2(assets, relationships, scope, "fingerprint", "fingerprint", 0, DateTimeOffset.UnixEpoch, relations.Integrations) : null;
        var json = JsonSerializer.Serialize(new { kinds = scope.Kinds.Select(Wire), tags = scope.Tags.Select(t => new[] { t.Key, t.Value }), items, relations = edges?.Relations, layers = edges?.Items.Select(i => i.Layer) }, Json);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    private async Task<SignedLandscapeDigest> BuildSignedAsync(LandscapeDigestScope scope, RelationOptions relations, CancellationToken ct)
    {
        var (assets, relationships) = await ReadAsync(ct);
        var (sourceInstanceId, sequence) = await state.NextAsync(context.Tenant, ct);
        var now = clock.GetUtcNow();
        // With relations off this is exactly the v1 digest of before. v2 is built only for an explicit, entitled opt-in.
        object digest = relations.Relations
            ? LandscapeDigestBuilder.BuildV2(assets, relationships, scope, DigestId(now), sourceInstanceId, sequence, now, relations.Integrations)
            : LandscapeDigestBuilder.Build(assets, relationships, scope, DigestId(now), sourceInstanceId, sequence, now);

        // The signature is over the canonical form of exactly the digest that is delivered.
        var canonical = Jcs.CanonicalizeUtf8(JsonSerializer.SerializeToNode(digest, Json)!);
        return new SignedLandscapeDigest(digest, await signer.SignAsync(canonical, ct));
    }

    /// <summary>The delivered file: the signed wrapper, in the same JSON options the canonical form was computed with.</summary>
    public static byte[] Render(SignedLandscapeDigest signed) => JsonSerializer.SerializeToUtf8Bytes(signed, new JsonSerializerOptions(Json) { WriteIndented = true });

    private void Authorize()
    {
        var decision = authorizer.Authorize(context.Tenant, context.Principal, AtlasActions.LandscapeShare, LandscapeResource);
        if (!decision.Allowed)
        {
            throw AccessDeniedException.FromAuthorization(decision, $"'{AtlasActions.LandscapeShare}' denied ({decision.ReasonCode}).");
        }
    }

    /// <summary>
    /// Relations need the separate Fabric grant on top of the sharing permission: a signed payload scope or a stored switch is never authorization.
    /// Denied with a reason code (not entitled) rather than silently dropping the edges.
    /// </summary>
    private void RequireRelations() => gate.Require(AtlasCapabilities.LandscapeShareRelations, LandscapeResource);

    private async Task<(ImmutableArray<Asset> Assets, ImmutableArray<Relationship> Relationships)> ReadAsync(CancellationToken ct) =>
        (await repository.ListAssetsAsync(context.Tenant, kind: null, ct), await repository.ListRelationshipsAsync(context.Tenant, ct));

    /// <summary>A time-ordered opaque id: sortable, unique, and carrying nothing about the tenant.</summary>
    private static string DigestId(DateTimeOffset now) => $"{now.ToUnixTimeMilliseconds():x}-{Guid.NewGuid():N}";

    private static string Wire(DigestKind kind) => JsonSerializer.Serialize(kind).Trim('"');

    private static string Wire(DigestRelationKind kind) => JsonSerializer.Serialize(kind).Trim('"');

    /// <summary>Parses the query form of a scope: kinds by name and tags as <c>key:value</c>. Unknown input is rejected, never ignored.</summary>
    public static LandscapeDigestScope ParseScope(IEnumerable<string>? kinds, IEnumerable<string>? tags)
    {
        var kindList = new List<DigestKind>();
        foreach (var text in (kinds ?? []).SelectMany(k => k.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (!Enum.TryParse<DigestKind>(text, ignoreCase: true, out var kind)) throw new ArgumentException($"Unknown kind '{text}'. Use system, application, platform or vendor.");
            if (!kindList.Contains(kind)) kindList.Add(kind);
        }

        var tagList = new List<Tag>();
        foreach (var text in (tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            var split = text.IndexOf(':');
            if (split <= 0 || split == text.Length - 1) throw new ArgumentException($"Tag '{text}' must be written key:value.");
            tagList.Add(new Tag(text[..split].Trim(), text[(split + 1)..].Trim()));
        }

        return new LandscapeDigestScope(kindList.Count == 0 ? LandscapeDigestBuilder.AllKinds : [.. kindList], [.. tagList]);
    }
}
