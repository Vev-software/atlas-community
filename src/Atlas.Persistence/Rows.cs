namespace Vev.Atlas.Persistence;

/// <summary>
/// Storage row for an asset. The full contract document is held as JSON (thin storage), with the
/// query-relevant fields promoted to columns. Tenant id is part of the key — isolation is enforced
/// in the schema, not merely in code (fabric#3, 05 §5).
/// </summary>
internal sealed class AssetRow
{
    public required string TenantId { get; set; }
    public required string Id { get; set; }
    public long NumericId { get; set; }
    public required string Kind { get; set; }
    public required string Name { get; set; }
    public required string Lifecycle { get; set; }

    /// <summary>The full <c>Vev.Atlas.Contracts.Asset</c> serialised with the canonical options.</summary>
    public required string DocumentJson { get; set; }

    /// <summary>Principal id of the user who created this asset (atlas#76).</summary>
    public string? CreatedBy { get; set; }
}

/// <summary>Storage row for a manual relationship. Tenant id is part of the key.</summary>
internal sealed class RelationshipRow
{
    public required string TenantId { get; set; }
    public required string Id { get; set; }
    public required string FromId { get; set; }
    public required string ToId { get; set; }
    public required string Type { get; set; }
    public string? Description { get; set; }
}

/// <summary>Tenant-scoped AI module settings including the encrypted BYOK secret.</summary>
internal sealed class AiModuleSettingsRow
{
    public required string TenantId { get; set; }
    public bool Enabled { get; set; }
    public bool ConsentAccepted { get; set; }
    public DateTimeOffset? ConsentAcceptedAt { get; set; }
    public string? ConsentAcceptedBy { get; set; }
    public string? Provider { get; set; }
    public string? EncryptedApiKey { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
