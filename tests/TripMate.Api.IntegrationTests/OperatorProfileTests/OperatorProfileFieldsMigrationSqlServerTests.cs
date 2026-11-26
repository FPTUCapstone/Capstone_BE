using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.OperatorProfileTests;

/// <summary>UC-34: dbo.OperatorProfiles gains five nullable business-profile columns.</summary>
public sealed class OperatorProfileFieldsMigrationSqlServerTests
{
    private const string MigrationFileName = "20261126_uc34_operator_profile_fields.sql";

    private static readonly (string Name, string Type, int MaxLength)[] ExpectedColumns =
    [
        ("description", "nvarchar", 2000),
        ("contact_email", "nvarchar", 200),
        ("website_url", "nvarchar", 300),
        ("logo_url", "nvarchar", 500),
        ("logo_public_id", "nvarchar", 300),
    ];

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FreshSchema_ContainsFiveNullableProfileColumns()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();

        foreach (var (name, type, maxLength) in ExpectedColumns)
        {
            (await ReadColumnAsync(database, name)).Should().Be($"{type}|{maxLength}|YES", name);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ExistingV7Data_MigrationAddsColumnsKeepsRowsAndIsIdempotent()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await DropProfileColumnsAsync(database);
        await InsertProfileAsync(database);

        await database.ExecuteScriptAsync(MigrationPath());
        await database.ExecuteScriptAsync(MigrationPath());

        foreach (var (name, type, maxLength) in ExpectedColumns)
        {
            (await ReadColumnAsync(database, name)).Should().Be($"{type}|{maxLength}|YES", name);
        }

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.OperatorProfiles
            WHERE company_name = N'UC34 existing'
              AND tax_code = N'TAX-UC34'
              AND business_license_no = N'LICENSE-UC34'
              AND description IS NULL AND contact_email IS NULL AND website_url IS NULL
              AND logo_url IS NULL AND logo_public_id IS NULL;
            """)).Should().Be(1);
    }

    private static Task DropProfileColumnsAsync(SqlServerTestDatabase database) =>
        database.ExecuteNonQueryAsync("""
            ALTER TABLE dbo.OperatorProfiles DROP COLUMN
                description, contact_email, website_url, logo_url, logo_public_id;
            """);

    private static Task InsertProfileAsync(SqlServerTestDatabase database) =>
        database.ExecuteNonQueryAsync("""
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('TourOperator', N'uc34-existing@example.com', N'UC34 existing', 'Active');

            INSERT dbo.OperatorProfiles(user_id, company_name, tax_code, business_license_no)
            VALUES (SCOPE_IDENTITY(), N'UC34 existing', N'TAX-UC34', N'LICENSE-UC34');
            """);

    private static Task<string?> ReadColumnAsync(SqlServerTestDatabase database, string column) =>
        database.ExecuteScalarAsync<string?>($"""
            SELECT CONCAT(data_type, '|', character_maximum_length, '|', is_nullable)
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE table_schema = N'dbo' AND table_name = N'OperatorProfiles'
              AND column_name = N'{column}';
            """);

    private static string MigrationPath() => Path.Combine(
        AppContext.BaseDirectory, "Database", "migrations", MigrationFileName);
}