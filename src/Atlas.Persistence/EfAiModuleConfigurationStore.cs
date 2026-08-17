using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Persistence;

/// <summary>
/// EF-backed tenant AI module settings store. The product owns the settings; the provider key is encrypted
/// at rest before it reaches SQLite.
/// </summary>
public sealed class EfAiModuleConfigurationStore(
    AtlasDbContext db,
    IDataProtectionProvider dataProtectionProvider) : IAiModuleConfigurationStore
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("atlas.ai.module-settings");

    public async ValueTask<AiModuleConfiguration?> GetAsync(TenantContext tenant, CancellationToken cancellationToken = default)
    {
        var row = await db.AiModuleSettings
            .SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        return new AiModuleConfiguration(
            Enabled: row.Enabled,
            ConsentAccepted: row.ConsentAccepted,
            ConsentAcceptedAt: row.ConsentAcceptedAt,
            ConsentAcceptedBy: row.ConsentAcceptedBy,
            Provider: row.Provider,
            ApiKey: string.IsNullOrWhiteSpace(row.EncryptedApiKey) ? null : _protector.Unprotect(row.EncryptedApiKey),
            UpdatedAt: row.UpdatedAt);
    }

    public async ValueTask SaveAsync(
        TenantContext tenant,
        AiModuleConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var row = await db.AiModuleSettings
            .SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId, cancellationToken);

        if (row is null)
        {
            row = new AiModuleSettingsRow { TenantId = tenant.TenantId };
            db.AiModuleSettings.Add(row);
        }

        row.Enabled = configuration.Enabled;
        row.ConsentAccepted = configuration.ConsentAccepted;
        row.ConsentAcceptedAt = configuration.ConsentAcceptedAt;
        row.ConsentAcceptedBy = configuration.ConsentAcceptedBy;
        row.Provider = configuration.Provider;
        row.EncryptedApiKey = string.IsNullOrWhiteSpace(configuration.ApiKey)
            ? null
            : _protector.Protect(configuration.ApiKey);
        row.UpdatedAt = configuration.UpdatedAt;

        await db.SaveChangesAsync(cancellationToken);
    }
}
