using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Vev.Atlas.Persistence;

/// <summary>
/// Applies EF migrations for fresh installs and upgrades legacy pre-migration SQLite databases in place.
/// Earlier Atlas Community builds used <c>EnsureCreated</c>, so an existing self-host may have tables but
/// no <c>__EFMigrationsHistory</c>; this upgrader backfills the numeric ids and then baselines the current
/// migration history so normal EF migration flow can continue afterwards.
/// </summary>
public static class AtlasDatabaseMigrator
{
    internal const string CurrentMigrationId = "20260817103803_AddAssetCreatedBy";
    private const string EfProductVersion = "10.0.11";

    public static async Task MigrateAsync(AtlasDbContext db, CancellationToken ct = default)
    {
        if (db.Database.IsSqlite())
        {
            await UpgradeLegacySqliteDatabaseIfNeededAsync(db, ct);
        }

        await db.Database.MigrateAsync(ct);
    }

    private static async Task UpgradeLegacySqliteDatabaseIfNeededAsync(AtlasDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        var hasAssetsTable = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'assets';",
            ct) > 0;
        if (!hasAssetsTable)
        {
            return;
        }

        var hasHistoryTable = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory';",
            ct) > 0;
        if (hasHistoryTable)
        {
            return;
        }

        await db.Database.BeginTransactionAsync(ct);
        try
        {
            var hasNumericIdColumn = await ScalarAsync<long>(
                connection,
                "SELECT COUNT(1) FROM pragma_table_info('assets') WHERE name = 'NumericId';",
                ct) > 0;
            if (!hasNumericIdColumn)
            {
                await db.Database.ExecuteSqlRawAsync(
                    """ALTER TABLE assets ADD COLUMN NumericId INTEGER NOT NULL DEFAULT 0;""",
                    ct);
            }

            var hasCreatedByColumn = await ScalarAsync<long>(
                connection,
                "SELECT COUNT(1) FROM pragma_table_info('assets') WHERE name = 'CreatedBy';",
                ct) > 0;
            if (!hasCreatedByColumn)
            {
                await db.Database.ExecuteSqlRawAsync(
                    """ALTER TABLE assets ADD COLUMN CreatedBy TEXT;""",
                    ct);
            }

            await db.Database.ExecuteSqlRawAsync(
                """
                WITH numbered AS (
                    SELECT rowid, ROW_NUMBER() OVER (PARTITION BY TenantId ORDER BY rowid) AS assigned
                    FROM assets
                )
                UPDATE assets
                SET NumericId = (
                    SELECT assigned
                    FROM numbered
                    WHERE numbered.rowid = assets.rowid
                )
                WHERE NumericId = 0;
                """,
                ct);

            await db.Database.ExecuteSqlRawAsync(
                """CREATE UNIQUE INDEX IF NOT EXISTS IX_assets_TenantId_NumericId ON assets (TenantId, NumericId);""",
                ct);
            await db.Database.ExecuteSqlRawAsync(
                """CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" ("MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY, "ProductVersion" TEXT NOT NULL);""",
                ct);
            // Insert all migration history entries so EF knows every migration has been applied.
            await db.Database.ExecuteSqlRawAsync(
                $"""INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('20260817075545_AddAssetNumericId', '{EfProductVersion}');""",
                ct);
            await db.Database.ExecuteSqlRawAsync(
                $"""INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('20260817082107_AddAiModuleSettings', '{EfProductVersion}');""",
                ct);
            await db.Database.ExecuteSqlRawAsync(
                $"""INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('{CurrentMigrationId}', '{EfProductVersion}');""",
                ct);

            await db.Database.CommitTransactionAsync(ct);
        }
        catch
        {
            await db.Database.RollbackTransactionAsync(ct);
            throw;
        }
    }

    private static async Task<T> ScalarAsync<T>(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(ct);
        return (T)Convert.ChangeType(result!, typeof(T));
    }
}
