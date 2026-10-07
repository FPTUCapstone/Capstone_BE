using System.Text.RegularExpressions;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Admin.Users;

public sealed class UserUnlockMigrationSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenAppliedToLegacyUsers_AddsCanonicalLockAndOperationContractsAndCanRunTwice()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            DROP TABLE IF EXISTS admin.UserUnlockOperations;
            ALTER TABLE dbo.Users DROP CONSTRAINT IF EXISTS FK_Users_LockedBy;
            ALTER TABLE dbo.Users DROP CONSTRAINT IF EXISTS CK_Users_LockRecoveryState;
            ALTER TABLE dbo.Users DROP COLUMN IF EXISTS status_before_lock;
            ALTER TABLE dbo.Users DROP COLUMN IF EXISTS locked_by_user_id;
            ALTER TABLE dbo.Users DROP COLUMN IF EXISTS locked_at_utc;
            ALTER TABLE dbo.Users DROP COLUMN IF EXISTS lock_reason;
            """);

        await ApplyMigrationAsync(database);
        await ApplyMigrationAsync(database);

        var lockColumnCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'dbo.Users')
              AND name IN (N'status_before_lock', N'locked_by_user_id', N'locked_at_utc', N'lock_reason');
            """);
        lockColumnCount.Should().Be(4);

        var invariantCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'dbo.Users')
              AND name = N'CK_Users_LockRecoveryState';
            """);
        invariantCount.Should().Be(1);

        var operationContractCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'admin.UserUnlockOperations')
              AND name = N'UX_UserUnlockOperations_AdministratorKey'
              AND is_unique = 1;
            """);
        operationContractCount.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenOperationIndexHasCanonicalNameButWrongShape_RebuildsCanonicalUniqueIndex()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            DROP INDEX IF EXISTS UX_UserUnlockOperations_AdministratorKey ON admin.UserUnlockOperations;
            CREATE INDEX UX_UserUnlockOperations_AdministratorKey
                ON admin.UserUnlockOperations(target_user_id);
            """);

        await ApplyMigrationAsync(database);

        var indexColumnCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.indexes AS indexDefinition
            INNER JOIN sys.index_columns AS indexColumn
                ON indexColumn.object_id = indexDefinition.object_id
               AND indexColumn.index_id = indexDefinition.index_id
            INNER JOIN sys.columns AS columnDefinition
                ON columnDefinition.object_id = indexColumn.object_id
               AND columnDefinition.column_id = indexColumn.column_id
            WHERE indexDefinition.object_id = OBJECT_ID(N'admin.UserUnlockOperations')
              AND indexDefinition.name = N'UX_UserUnlockOperations_AdministratorKey'
              AND indexDefinition.is_unique = 1
              AND indexColumn.is_included_column = 0
              AND ((indexColumn.key_ordinal = 1 AND columnDefinition.name = N'administrator_user_id')
                   OR (indexColumn.key_ordinal = 2 AND columnDefinition.name = N'idempotency_key'));
            """);
        indexColumnCount.Should().Be(2);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenLegacyLockStatusBeforeLockIsUnknown_NormalizesTheIncompleteRecoveryState()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE dbo.Users DROP CONSTRAINT CK_Users_LockRecoveryState;

            INSERT INTO dbo.Users (role, email, full_name, status)
            VALUES ('Administrator', 'unlock-migration-admin@example.com', 'Migration administrator', 'Active');
            DECLARE @administratorId BIGINT = SCOPE_IDENTITY();

            INSERT INTO dbo.Users (
                role, email, full_name, status, status_before_lock,
                locked_by_user_id, locked_at_utc, lock_reason)
            VALUES (
                'Traveler', 'unlock-migration-invalid@example.com', 'Invalid recovery state', 'Locked', 'UnknownStatus',
                @administratorId, SYSUTCDATETIME(), 'Legacy lock');
            """);

        await ApplyMigrationAsync(database);

        var normalizedRowCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM dbo.Users
            WHERE email = 'unlock-migration-invalid@example.com'
              AND status_before_lock IS NULL
              AND locked_by_user_id IS NULL
              AND locked_at_utc IS NULL
              AND lock_reason IS NULL;
            """);
        normalizedRowCount.Should().Be(1);
    }

    private static async Task ApplyMigrationAsync(SqlServerTestDatabase database)
    {
        var migrationPath = Path.Combine(
            AppContext.BaseDirectory,
            "Database",
            "migrations",
            "20261007_add_user_unlock_operations.sql");
        var migration = await File.ReadAllTextAsync(migrationPath);
        migration = Regex.Replace(migration, @"^\s*GO\s*$", string.Empty, RegexOptions.Multiline);
        await database.ExecuteNonQueryAsync(migration);
    }
}