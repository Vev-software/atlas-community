using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Vev.Atlas.Domain.Sharing;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Persistence;

/// <summary>Per-tenant source identity and sequence of the share digest (replay protection). Tenant id is part of the key.</summary>
internal sealed class DigestStateRow
{
    public required string TenantId { get; set; }
    public required string SourceInstanceId { get; set; }
    public int Sequence { get; set; }
}

/// <summary>The installation's digest signing key. Not tenant-scoped: one key signs for the installation. The private key is stored protected.</summary>
internal sealed class DigestKeyRow
{
    public required string Id { get; set; }
    public required string KeyId { get; set; }
    public required string ProtectedPrivateKey { get; set; }
    public required string PublicKeySpki { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Reserves digest sequence numbers. The source id is minted once per tenant and is a random opaque value, so it says
/// nothing about the tenant. Reservation is serialized in-process, which is enough for the single-node Community build.
/// </summary>
public sealed class EfDigestStateStore(AtlasDbContext db) : IDigestStateStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<(string SourceInstanceId, int Sequence)> NextAsync(TenantContext tenant, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var row = await db.DigestStates.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId, ct);
            if (row is null)
            {
                row = new DigestStateRow { TenantId = tenant.TenantId, SourceInstanceId = $"atlas-{Guid.NewGuid():N}", Sequence = 0 };
                db.DigestStates.Add(row);
            }

            row.Sequence++;
            await db.SaveChangesAsync(ct);
            return (row.SourceInstanceId, row.Sequence);
        }
        finally
        {
            Gate.Release();
        }
    }
}

/// <summary>
/// Signs digests with an installation key created on first use (RSA 3072, PKCS#1 v1.5, SHA-256). The private key is
/// protected with the application's data protection keys before it reaches the database, like the AI provider key.
/// A consumer pins the public key it received with the first digest; the key id is stable for the life of the key.
/// </summary>
public sealed class EfDigestSigner(AtlasDbContext db, IDataProtectionProvider dataProtectionProvider, TimeProvider clock) : IDigestSigner
{
    private const string RowId = "installation";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("atlas.share.digest-key");

    public async Task<DigestSignature> SignAsync(byte[] canonicalDigest, CancellationToken ct = default)
    {
        var row = await LoadOrCreateAsync(ct);
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(_protector.Unprotect(row.ProtectedPrivateKey)), out _);
        var signature = rsa.SignData(canonicalDigest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return new DigestSignature(LandscapeShareService.SignatureAlgorithm, row.KeyId, row.PublicKeySpki, ToBase64Url(signature));
    }

    private async Task<DigestKeyRow> LoadOrCreateAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var existing = await db.DigestKeys.SingleOrDefaultAsync(k => k.Id == RowId, ct);
            if (existing is not null) return existing;

            using var rsa = RSA.Create(3072);
            var row = new DigestKeyRow
            {
                Id = RowId,
                KeyId = $"atlas-digest-{Guid.NewGuid():N}",
                ProtectedPrivateKey = _protector.Protect(Convert.ToBase64String(rsa.ExportPkcs8PrivateKey())),
                PublicKeySpki = ToBase64Url(rsa.ExportSubjectPublicKeyInfo()),
                CreatedAt = clock.GetUtcNow(),
            };
            db.DigestKeys.Add(row);
            await db.SaveChangesAsync(ct);
            return row;
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static string ToBase64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
