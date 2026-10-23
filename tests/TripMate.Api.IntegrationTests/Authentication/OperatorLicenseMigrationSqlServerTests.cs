using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Authentication;

public sealed class OperatorLicenseMigrationSqlServerTests
{
    private const string IndexName = "UX_OperatorProfiles_BusinessLicenseNo";
    private const string MigrationFileName = "20261002_add_operator_license_unique.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FreshSchema_ContainsUniqueFilteredLicenseIndex()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();

        (await ReadIndexCountAsync(database)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ExistingV7Data_MigrationPreservesRowsAndIsIdempotent()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync($"DROP INDEX {IndexName} ON dbo.OperatorProfiles;");
        await InsertProfileAsync(database, "first", "LICENSE-UC02-1");

        await database.ExecuteScriptAsync(MigrationPath());
        await database.ExecuteScriptAsync(MigrationPath());

        (await ReadIndexCountAsync(database)).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.OperatorProfiles
            WHERE business_license_no = N'LICENSE-UC02-1';
            """)).Should().Be(1);

        Func<Task> insertDuplicate = () => InsertProfileAsync(
            database, "second", "LICENSE-UC02-1");
        (await insertDuplicate.Should().ThrowAsync<SqlException>())
            .Which.Number.Should().Be(2601);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task DuplicateLegacyLicenses_StopMigrationWithoutChangingData()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync($"DROP INDEX {IndexName} ON dbo.OperatorProfiles;");
        await InsertProfileAsync(database, "first", "LICENSE-UC02-DUP");
        await InsertProfileAsync(database, "second", "LICENSE-UC02-DUP");

        Func<Task> apply = () => database.ExecuteScriptAsync(MigrationPath());
        (await apply.Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("duplicate business licence numbers");

        (await ReadIndexCountAsync(database)).Should().Be(0);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.OperatorProfiles
            WHERE business_license_no = N'LICENSE-UC02-DUP';
            """)).Should().Be(2);
    }

    private static Task InsertProfileAsync(
        SqlServerTestDatabase database,
        string suffix,
        string businessLicenseNo) => database.ExecuteNonQueryAsync($"""
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('TourOperator', N'uc02-{suffix}@example.com', N'UC02 {suffix}', 'PendingApproval');

            INSERT dbo.OperatorProfiles(
                user_id, company_name, tax_code, business_license_no)
            VALUES (
                SCOPE_IDENTITY(), N'UC02 {suffix}', N'TAX-UC02-{suffix}',
                N'{businessLicenseNo}');
            """);

    private static Task<int> ReadIndexCountAsync(SqlServerTestDatabase database) =>
        database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*)
            FROM sys.indexes AS index_info
            INNER JOIN sys.index_columns AS index_column
                ON index_column.object_id = index_info.object_id
                AND index_column.index_id = index_info.index_id
            INNER JOIN sys.columns AS column_info
                ON column_info.object_id = index_column.object_id
                AND column_info.column_id = index_column.column_id
            WHERE index_info.object_id = OBJECT_ID(N'dbo.OperatorProfiles')
              AND index_info.name = N'{IndexName}'
              AND index_info.is_unique = 1
              AND index_info.has_filter = 1
              AND index_column.key_ordinal = 1
              AND column_info.name = N'business_license_no';
            """);

    private static string MigrationPath() => Path.Combine(
        AppContext.BaseDirectory, "Database", "migrations", MigrationFileName);
}