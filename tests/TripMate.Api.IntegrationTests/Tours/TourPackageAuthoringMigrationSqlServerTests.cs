using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Tours;

public sealed class TourPackageAuthoringMigrationSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CurrentSchema_ContainsUc35AuthoringTablesColumnsAndPublishedStatus()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();

        var scheduleColumns = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'commerce.TourSchedules')
              AND name IN (N'minimum_capacity', N'registration_deadline_at',
                           N'adult_price', N'child_price', N'infant_price');
            """);
        scheduleColumns.Should().Be(5);

        var authoringTables = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.tables AS table_definition
            INNER JOIN sys.schemas AS schema_definition
                ON schema_definition.schema_id = table_definition.schema_id
            WHERE schema_definition.name = N'commerce'
              AND table_definition.name IN (
                  N'CancellationPolicyTemplates',
                  N'CancellationPolicyTemplateRules',
                  N'TourCancellationPolicyRules',
                  N'TourPackageCreateOperations');
            """);
        authoringTables.Should().Be(4);

        await database.ExecuteNonQueryAsync("""
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('TourOperator', N'uc35-migration@test.local', N'UC-35 migration operator', 'Active');

            DECLARE @operatorUserId BIGINT = SCOPE_IDENTITY();
            INSERT dbo.OperatorProfiles(
                user_id, company_name, tax_code, business_license_no, approval_status)
            VALUES (
                @operatorUserId, N'UC-35 Tours', N'UC35MIGRATION', N'UC35-LICENSE', 'Approved');

            INSERT commerce.Tours(
                operator_user_id, title, description, base_price, duration_days, status)
            VALUES (@operatorUserId, N'Published UC-35 migration validation', N'Test', 100000.00, 1, 'Published');
            """);

        var publishedTours = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM commerce.Tours
            WHERE title = N'Published UC-35 migration validation'
              AND status = 'Published';
            """);
        publishedTours.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task LegacyScheduleInsert_WithoutMinimumCapacity_DefaultsToOne()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('TourOperator', N'uc35-legacy@test.local', N'UC-35 legacy operator', 'Active');

            DECLARE @operatorUserId BIGINT = SCOPE_IDENTITY();
            INSERT dbo.OperatorProfiles(
                user_id, company_name, tax_code, business_license_no, approval_status)
            VALUES (
                @operatorUserId, N'UC-35 Legacy Tours', N'UC35LEGACY', N'UC35-LEGACY-LICENSE', 'Approved');

            INSERT commerce.Tours(
                operator_user_id, title, description, base_price, duration_days, status)
            VALUES (@operatorUserId, N'Legacy schedule writer', N'Test', 100000.00, 1, 'Approved');

            INSERT commerce.TourSchedules(tour_id, start_datetime, end_datetime, total_capacity)
            VALUES (SCOPE_IDENTITY(), '2030-01-01T08:00:00', '2030-01-01T18:00:00', 12);
            """);

        var minimumCapacity = await database.ExecuteScalarAsync<int>(
            "SELECT minimum_capacity FROM commerce.TourSchedules;");

        minimumCapacity.Should().Be(1);
    }
}