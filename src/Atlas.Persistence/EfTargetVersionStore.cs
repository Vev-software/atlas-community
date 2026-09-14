using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vev.Atlas.Contracts;
using Vev.Atlas.Domain;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Persistence;

internal sealed class TargetVersionRow
{
    public string TenantId { get; set; } = "";
    public string Id { get; set; } = "";
    public string DocumentJson { get; set; } = "";
}

public sealed class EfTargetVersionStore(AtlasDbContext db) : ITargetVersionStore
{
    public async Task<IReadOnlyList<TargetVersion>> ListAsync(TenantContext tenant, CancellationToken ct)
    {
        var rows = await db.TargetVersions.AsNoTracking().Where(v => v.TenantId == tenant.TenantId).ToListAsync(ct);
        return rows.Select(r => JsonSerializer.Deserialize<TargetVersion>(r.DocumentJson, AtlasContracts.SerializerOptions)!)
            .OrderBy(v => v.SavedAt).ToArray();
    }

    public Task<int> CountAsync(TenantContext tenant, CancellationToken ct) =>
        db.TargetVersions.CountAsync(v => v.TenantId == tenant.TenantId, ct);

    public async Task<bool> TrySaveAsync(TenantContext tenant, TargetVersion version, int? limit, CancellationToken ct)
    {
        // SQLite's immediate write transaction serializes count + insert across connections/processes.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (limit is { } cap && await CountAsync(tenant, ct) >= cap) return false;
        db.TargetVersions.Add(new TargetVersionRow
        {
            TenantId = tenant.TenantId,
            Id = version.Id,
            DocumentJson = JsonSerializer.Serialize(version, AtlasContracts.SerializerOptions)
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
