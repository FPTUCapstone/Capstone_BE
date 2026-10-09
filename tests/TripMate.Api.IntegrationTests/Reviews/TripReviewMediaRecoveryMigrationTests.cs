using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Reviews;

public sealed class TripReviewMediaRecoveryMigrationTests
{
    internal const string Migration = "20260930_add_trip_review_media_recovery.sql";
    private static Task Migrate(SqlServerTestDatabase db) => db.ExecuteScriptAsync(Path.Combine(AppContext.BaseDirectory, "Database", "migrations", Migration));
    private static async Task<SqlServerTestDatabase> Baseline()
    {
        var db = await TripReviewMediaMigrationTests.Baseline();
        try { await TripReviewMediaMigrationTests.Migrate(db); return db; }
        catch { await db.DisposeAsync(); throw; }
    }
    [SqlServerFact]
    public async Task FreshAndUpgradeInventoryMatch_RerunPreservesConservativeBackfill()
    {
        await using var fresh = await SqlServerTestDatabase.CreateAsync();
        await using var upgrade = await Baseline();
        var reserved = Guid.NewGuid(); var uploaded = Guid.NewGuid();
        var cleanupKnown = Guid.NewGuid(); var cleanupUnknown = Guid.NewGuid();
        await upgrade.ExecuteNonQueryAsync(
            TripReviewMediaMigrationTests.Insert(reserved, Guid.NewGuid())
            + TripReviewMediaMigrationTests.Insert(uploaded, Guid.NewGuid())
            + TripReviewMediaMigrationTests.Insert(cleanupKnown, Guid.NewGuid())
            + TripReviewMediaMigrationTests.Insert(cleanupUnknown, Guid.NewGuid())
            + $"UPDATE social.TripReviewMediaOperations SET state='Uploaded',uploaded_at='2026-09-29',delivery_url=N'https://example.invalid/a',stored_byte_length=1 WHERE operation_id='{uploaded}';"
            + $"UPDATE social.TripReviewMediaOperations SET state='CleanupPending',uploaded_at='2026-09-29',delivery_url=N'https://example.invalid/b',stored_byte_length=1 WHERE operation_id='{cleanupKnown}';"
            + $"UPDATE social.TripReviewMediaOperations SET state='CleanupPending' WHERE operation_id='{cleanupUnknown}';");
        var before = await upgrade.ExecuteScalarAsync<string>("SELECT (SELECT * FROM social.TripReviewMediaOperations FOR JSON PATH)");
        await Migrate(upgrade);
        (await TripReviewSchemaInventory.ReadAsync(fresh, recoveryOnly: true)).Should().Equal(await TripReviewSchemaInventory.ReadAsync(upgrade, recoveryOnly: true));
        (await upgrade.ExecuteScalarAsync<string>($"SELECT upload_outcome FROM social.TripReviewMediaRecovery WHERE operation_id='{reserved}'")).Should().Be("Unknown");
        (await upgrade.ExecuteScalarAsync<string>($"SELECT upload_outcome FROM social.TripReviewMediaRecovery WHERE operation_id='{uploaded}'")).Should().Be("Succeeded");
        (await upgrade.ExecuteScalarAsync<string>($"SELECT upload_outcome FROM social.TripReviewMediaRecovery WHERE operation_id='{cleanupKnown}'")).Should().Be("Succeeded");
        (await upgrade.ExecuteScalarAsync<string>($"SELECT upload_outcome FROM social.TripReviewMediaRecovery WHERE operation_id='{cleanupUnknown}'")).Should().Be("Unknown");
        var recovery = await upgrade.ExecuteScalarAsync<string>("SELECT (SELECT * FROM social.TripReviewMediaRecovery ORDER BY operation_id FOR JSON PATH)");
        await Migrate(upgrade);
        (await upgrade.ExecuteScalarAsync<string>("SELECT (SELECT * FROM social.TripReviewMediaRecovery ORDER BY operation_id FOR JSON PATH)")).Should().Be(recovery);
        (await upgrade.ExecuteScalarAsync<string>("SELECT (SELECT * FROM social.TripReviewMediaOperations FOR JSON PATH)")).Should().Be(before);
        await TripReviewMediaMigrationTests.Migrate(upgrade);
    }
    [SqlServerTheory]
    [InlineData("ALTER TABLE social.TripReviewMediaRecovery ADD unexpected int NULL;")]
    [InlineData("ALTER TABLE social.TripReviewMediaRecovery ALTER COLUMN last_failure_code VARCHAR(48) COLLATE Latin1_General_100_BIN2 NULL;")]
    [InlineData("ALTER TABLE social.TripReviewMediaRecovery DROP CONSTRAINT DF_TripReviewMediaRecovery_Attempts;")]
    [InlineData("CREATE INDEX IX_Unexpected ON social.TripReviewMediaRecovery(upload_outcome);")]
    [InlineData("ALTER TABLE social.TripReviewMediaRecovery NOCHECK CONSTRAINT ALL;")]
    [InlineData("ALTER TABLE social.TripReviewMediaRecovery DROP CONSTRAINT FK_TripReviewMediaRecovery_Operation;")]
    [InlineData("DROP INDEX IX_TripReviewMediaRecovery_Due ON social.TripReviewMediaRecovery;")]
    public async Task WrongShapeIsRejectedWithoutRepair(string mutation)
    {
        await using var db = await Baseline(); await Migrate(db); await db.ExecuteNonQueryAsync(mutation);
        var before = await TripReviewSchemaInventory.ReadAsync(db, recoveryOnly: true);
        Func<Task> run = () => Migrate(db); await run.Should().ThrowAsync<SqlException>();
        (await TripReviewSchemaInventory.ReadAsync(db, recoveryOnly: true)).Should().Equal(before);
    }
    [SqlServerTheory]
    [InlineData("attempts=-1")]
    [InlineData("attempts=9")]
    [InlineData("upload_outcome='Timeout'")]
    [InlineData("upload_fence=NEWID()")]
    [InlineData("cleanup_fence=NEWID()")]
    [InlineData("last_failure_code='raw provider exception'")]
    [InlineData("exhausted=1,next_attempt_at=SYSUTCDATETIME()")]
    public async Task InvalidRecoveryMetadataIsRejected(string mutation)
    {
        await using var db = await Baseline(); var id = Guid.NewGuid();
        await db.ExecuteNonQueryAsync(TripReviewMediaMigrationTests.Insert(id, Guid.NewGuid())); await Migrate(db);
        Func<Task> run = () => db.ExecuteNonQueryAsync("UPDATE social.TripReviewMediaRecovery SET " + mutation);
        await run.Should().ThrowAsync<SqlException>();
    }
    [SqlServerFact]
    public async Task RecoveryForeignKeyRestrictsPhysicalOperationDeletion()
    {
        await using var db = await Baseline(); var id = Guid.NewGuid();
        await db.ExecuteNonQueryAsync(TripReviewMediaMigrationTests.Insert(id, Guid.NewGuid())); await Migrate(db);
        Func<Task> run = () => db.ExecuteNonQueryAsync("DELETE social.TripReviewMediaOperations");
        await run.Should().ThrowAsync<SqlException>();
    }
}