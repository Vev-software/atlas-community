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
    [property: JsonPropertyName("digest")] LandscapeDigest Digest,
    [property: JsonPropertyName("signature")] DigestSignature Signature);

/// <summary>
/// What a digest would hold, shown before anything leaves the installation. <see cref="Shared"/> and
/// <see cref="NeverShared"/> state the field-level rule so the sharer sees what is, and what can never be, in the file.
/// </summary>
public sealed record DigestPreview(
    LandscapeDigestScope Scope,
    ImmutableArray<LandscapeDigestItem> Items,
    ImmutableArray<string> Shared,
    ImmutableArray<string> NeverShared);

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

    public static readonly ImmutableArray<string> NeverSharedFields =
        ["descriptions", "hostnames and IP addresses", "servers' environment and operating system", "endpoints and locations", "owners and people", "attachments",
         "data areas, datasets and columns", "relationship details", "credentials"];

    public async Task<DigestPreview> PreviewAsync(LandscapeDigestScope scope, CancellationToken ct = default)
    {
        Authorize();
        var (assets, relationships) = await ReadAsync(ct);
        return new DigestPreview(scope, LandscapeDigestBuilder.BuildItems(assets, relationships, scope), SharedFields, NeverSharedFields);
    }

    public async Task<SignedLandscapeDigest> CreateAsync(LandscapeDigestScope scope, CancellationToken ct = default)
    {
        Authorize();
        var (assets, relationships) = await ReadAsync(ct);
        var (sourceInstanceId, sequence) = await state.NextAsync(context.Tenant, ct);
        var now = clock.GetUtcNow();
        var digest = LandscapeDigestBuilder.Build(assets, relationships, scope, DigestId(now), sourceInstanceId, sequence, now);

        // The signature is over the canonical form of exactly the digest that is delivered.
        var canonical = Jcs.CanonicalizeUtf8(JsonSerializer.SerializeToNode(digest, Json)!);
        var signature = await signer.SignAsync(canonical, ct);

        // Counts and kinds only: no item name or customer content in the audit trail.
        await audit.WriteAsync(AtlasAudit.Event(context, clock, "atlas.landscape.shared",
            $"atlas:landscape/share?digest={digest.DigestId}&kinds={string.Join(',', scope.Kinds.Select(Wire))}&tags={scope.Tags.Length}&items={digest.Items.Length}&sequence={sequence}"), ct);
        return new SignedLandscapeDigest(digest, signature);
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

    private async Task<(ImmutableArray<Asset> Assets, ImmutableArray<Relationship> Relationships)> ReadAsync(CancellationToken ct) =>
        (await repository.ListAssetsAsync(context.Tenant, kind: null, ct), await repository.ListRelationshipsAsync(context.Tenant, ct));

    /// <summary>A time-ordered opaque id: sortable, unique, and carrying nothing about the tenant.</summary>
    private static string DigestId(DateTimeOffset now) => $"{now.ToUnixTimeMilliseconds():x}-{Guid.NewGuid():N}";

    private static string Wire(DigestKind kind) => JsonSerializer.Serialize(kind).Trim('"');

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
