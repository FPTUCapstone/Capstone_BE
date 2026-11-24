using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Tours;

public sealed class TourSubmissionMigrationSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_RejectsWrongShapeWithoutPartialChanges()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("CREATE SCHEMA commerce;");
        var path = Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "20261118_tm83_tour_submission.sql");
        string[] definitions =
        [
            "submitted_at_utc DATETIMEOFFSET NULL",
            "submitted_at_utc DATETIME2(3) NULL",
            "submitted_at_utc DATETIME2 NOT NULL",
            "submission_note VARCHAR(4000) NULL",
            "submission_note NVARCHAR(2000) NULL",
            "submission_note NVARCHAR(4000) NOT NULL",
        ];
        foreach (var definition in definitions)
        {
            await database.ExecuteNonQueryAsync("CREATE TABLE commerce.Tours(tour_id BIGINT NOT NULL, " + definition + ");");
            Func<Task> migrate = () => database.ExecuteScriptAsync(path);
            await migrate.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>(definition);
            (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('commerce.Tours') AND name IN ('submitted_at_utc','submission_note');")).Should().Be(1);
            await database.ExecuteNonQueryAsync("DROP TABLE commerce.Tours;");
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_IsAdditiveAndIdempotent_WithoutInventingLegacySubmissionTimes()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE commerce.Tours DROP COLUMN submitted_at_utc, submission_note;
            INSERT dbo.Users(role,email,full_name,status) VALUES ('TourOperator','tm83-migration@test.local','Operator','Active');
            DECLARE @id BIGINT = SCOPE_IDENTITY();
            INSERT dbo.OperatorProfiles(user_id,company_name,tax_code,business_license_no,approval_status)
            VALUES (@id,'Tours','TM83-TAX','TM83-LICENSE','Approved');
            INSERT commerce.Tours(operator_user_id,title,description,base_price,duration_days,status)
            VALUES (@id,'Legacy Pending','Description',100000,1,'Pending');
            """);
        var path = Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "20261118_tm83_tour_submission.sql");
        await database.ExecuteScriptAsync(path);
        await database.ExecuteScriptAsync(path);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM commerce.Tours WHERE status='Pending' AND submitted_at_utc IS NULL AND submission_note IS NULL;")).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('commerce.Tours') AND ((name='submitted_at_utc' AND TYPE_NAME(system_type_id)='datetime2') OR (name='submission_note' AND max_length=8000)) AND is_nullable=1;")).Should().Be(2);
    }
}