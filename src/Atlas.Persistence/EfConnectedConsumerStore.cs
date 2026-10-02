using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Vev.Atlas.Contracts;
using Vev.Atlas.Domain.Sharing;
using Vev.Atlas.Domain.Sharing.Push;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Persistence;

/// <summary>A consumer an admin connected for outbound pushes. Tenant id is part of the key; the credential is stored protected.</summary>
internal sealed class ConnectedConsumerRow
{
    public required string TenantId { get; set; }
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string DestinationUrl { get; set; }
    public required string EnrollmentId { get; set; }
    public required string State { get; set; }
    public string? StopReason { get; set; }
    public required string KindsJson { get; set; }
    public required string TagsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset? CredentialExpiresAt { get; set; }
    public string? ProtectedCredential { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public int? LastSuccessSequence { get; set; }
    public string? LastError { get; set; }
    public int FailureCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastContentFingerprint { get; set; }
}

/// <summary>EF-backed connected consumers. The credential is protected with the data protection keys, like the AI provider key.</summary>
public sealed class EfConnectedConsumerStore(AtlasDbContext db, IDataProtectionProvider dataProtectionProvider) : IConnectedConsumerStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("atlas.share.consumer-credential");

    public async Task<ImmutableArray<ConnectedConsumer>> ListAsync(TenantContext tenant, CancellationToken ct = default) =>
        [.. (await db.ConnectedConsumers.AsNoTracking().Where(r => r.TenantId == tenant.TenantId).ToListAsync(ct))
            .OrderBy(r => r.CreatedAt).Select(ToConsumer)];

    public async Task<ConnectedConsumer?> GetAsync(TenantContext tenant, string id, CancellationToken ct = default)
    {
        var row = await db.ConnectedConsumers.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.Id == id, ct);
        return row is null ? null : ToConsumer(row);
    }

    public async Task AddAsync(TenantContext tenant, ConnectedConsumer consumer, string credential, CancellationToken ct = default)
    {
        var row = new ConnectedConsumerRow
        {
            TenantId = tenant.TenantId,
            Id = consumer.Id,
            Name = consumer.Name,
            DestinationUrl = consumer.DestinationUrl,
            EnrollmentId = consumer.EnrollmentId,
            State = consumer.State.ToString(),
            KindsJson = "[]",
            TagsJson = "[]",
            CreatedAt = consumer.CreatedAt,
            CreatedBy = consumer.CreatedBy,
            ProtectedCredential = _protector.Protect(credential),
        };
        Apply(row, consumer);
        db.ConnectedConsumers.Add(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(TenantContext tenant, ConnectedConsumer consumer, CancellationToken ct = default)
    {
        var row = await db.ConnectedConsumers.SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.Id == consumer.Id, ct)
            ?? throw new InvalidOperationException($"Consumer '{consumer.Id}' not found for update.");
        Apply(row, consumer);
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> GetCredentialAsync(TenantContext tenant, string id, CancellationToken ct = default)
    {
        var protectedCredential = await db.ConnectedConsumers.AsNoTracking().Where(r => r.TenantId == tenant.TenantId && r.Id == id)
            .Select(r => r.ProtectedCredential).SingleOrDefaultAsync(ct);
        return string.IsNullOrEmpty(protectedCredential) ? null : _protector.Unprotect(protectedCredential);
    }

    public async Task ClearCredentialAsync(TenantContext tenant, string id, CancellationToken ct = default)
    {
        var row = await db.ConnectedConsumers.SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.Id == id, ct);
        if (row is null) return;
        row.ProtectedCredential = null;
        await db.SaveChangesAsync(ct);
    }

    public async Task<ImmutableArray<DueConsumer>> ListDueAsync(DateTimeOffset now, TimeSpan staleAfter, IReadOnlySet<string> changedTenants, CancellationToken ct = default)
    {
        // cross-tenant: the scheduler must see every tenant's due consumers; it returns ids only and each push then runs inside its own tenant.
        var rows = await db.ConnectedConsumers.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.State == nameof(ConnectedConsumerState.Active))
            .Select(r => new { r.TenantId, r.Id, r.NextAttemptAt, r.LastSuccessAt })
            .ToListAsync(ct);
        // Dates are compared here, not in SQL: SQLite stores them as text.
        // A scheduled retry is due when its time has come, whatever else is true. Otherwise a consumer is due when it has never succeeded, its last
        // success is older than the daily interval, or its tenant's landscape changed.
        return
        [
            .. rows
                .Where(r => r.NextAttemptAt is { } next
                    ? next <= now
                    : r.LastSuccessAt is null || now - r.LastSuccessAt >= staleAfter || changedTenants.Contains(r.TenantId))
                .Select(r => new DueConsumer(r.TenantId, r.Id))
        ];
    }

    private static void Apply(ConnectedConsumerRow row, ConnectedConsumer c)
    {
        row.Name = c.Name;
        row.State = c.State.ToString();
        row.StopReason = c.StopReason;
        row.KindsJson = JsonSerializer.Serialize(c.Scope.Kinds, Json);
        row.TagsJson = JsonSerializer.Serialize(c.Scope.Tags, Json);
        row.CredentialExpiresAt = c.CredentialExpiresAt;
        row.LastAttemptAt = c.LastAttemptAt;
        row.LastSuccessAt = c.LastSuccessAt;
        row.LastSuccessSequence = c.LastSuccessSequence;
        row.LastError = c.LastError;
        row.FailureCount = c.FailureCount;
        row.NextAttemptAt = c.NextAttemptAt;
        row.LastContentFingerprint = c.LastContentFingerprint;
    }

    private static ConnectedConsumer ToConsumer(ConnectedConsumerRow r) => new(
        r.Id, r.Name, r.DestinationUrl, r.EnrollmentId, Enum.Parse<ConnectedConsumerState>(r.State),
        new LandscapeDigestScope(JsonSerializer.Deserialize<ImmutableArray<DigestKind>>(r.KindsJson, Json), JsonSerializer.Deserialize<ImmutableArray<Tag>>(r.TagsJson, Json)),
        r.CreatedAt, r.CreatedBy, r.CredentialExpiresAt, r.StopReason, r.LastAttemptAt, r.LastSuccessAt, r.LastSuccessSequence, r.LastError,
        r.FailureCount, r.NextAttemptAt, r.LastContentFingerprint);
}
