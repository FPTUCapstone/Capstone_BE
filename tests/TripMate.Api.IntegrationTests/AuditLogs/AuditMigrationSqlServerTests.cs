using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.AuditLogs.GetList;
using TripMate.Application.Features.PointsOfInterest.Create;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.AuditLogs;

[Collection(nameof(TripMateApiFactory))]
public sealed class AuditMigrationSqlServerTests
{
    private static readonly DateTimeOffset Boundary = new(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FreshSchema_AndLegacyUpgradeTwice_PreserveDataAndMatchSchema()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var freshColumns = await ReadColumnInventory(database);
        freshColumns.Should().Contain("result:varchar:20:1");
        freshColumns.Should().Contain("reason:nvarchar:2000:1");

        // Only this helper-created disposable database is modified, never the configured database.
        database.DatabaseName.Should().StartWith("TripMate_Test_");
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE dbo.AuditLogs DROP CONSTRAINT CK_AuditLogs_Result;
            ALTER TABLE dbo.AuditLogs DROP COLUMN result, reason;
            INSERT dbo.AuditLogs(action_type, affected_entity, affected_entity_id, before_data, after_data, created_at)
            VALUES ('Legacy', 'POI', 42, NULL, N'{"reason":"Không suy đoán"}', '2026-09-18T00:00:00');
            """);
        var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Database", "migrations", "20260918_add_audit_result_reason.sql"));
        await database.ExecuteNonQueryAsync(migration);
        var firstColumns = await ReadColumnInventory(database);
        firstColumns.Should().BeEquivalentTo(freshColumns);
        await using (var db = database.CreateDbContext())
        {
            var legacy = await db.AuditLogs.AsNoTracking().SingleAsync();
            legacy.Id.Should().Be(1);
            legacy.ActorUserId.Should().BeNull();
            legacy.Result.Should().BeNull();
            legacy.Reason.Should().BeNull();
            legacy.BeforeData.Should().BeNull();
            legacy.AfterData.Should().Be("{\"reason\":\"Không suy đoán\"}");
            legacy.CreatedAtUtc.Should().Be(Boundary);
        }
        await database.ExecuteNonQueryAsync(migration);
        (await ReadColumnInventory(database)).Should().BeEquivalentTo(firstColumns);
        await using (var db = database.CreateDbContext())
        {
            var legacy = await db.AuditLogs.AsNoTracking().SingleAsync();
            legacy.Id.Should().Be(1);
            legacy.Result.Should().BeNull();
            legacy.Reason.Should().BeNull();
            legacy.AfterData.Should().Be("{\"reason\":\"Không suy đoán\"}");
        }
        var invalidOutcome = () => database.ExecuteNonQueryAsync("UPDATE dbo.AuditLogs SET result = 'Unknown';");
        (await invalidOutcome.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(547);
        var excessiveReason = () => database.ExecuteNonQueryAsync("SET ANSI_WARNINGS ON; UPDATE dbo.AuditLogs SET reason = REPLICATE(N'x', 1001);");
        (await excessiveReason.Should().ThrowAsync<SqlException>()).Which.Number.Should().BeOneOf(8152, 2628);
        await database.ExecuteNonQueryAsync("UPDATE dbo.AuditLogs SET result = 'Success', reason = N'Lý do hợp lệ';");
        await using var verification = database.CreateDbContext();
        var updated = await verification.AuditLogs.AsNoTracking().SingleAsync();
        updated.Result.Should().Be(AuditOutcome.Success);
        updated.Reason.Should().Be("Lý do hợp lệ");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CheckInJournalMigration_LegacyUpgradeTwice_PreservesRowsAndEnforcesEventUniqueness()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Database", "migrations", "20261105_add_checkin_audit_journal_identity.sql"));

        await database.ExecuteNonQueryAsync("""
            DROP INDEX UX_AuditLogs_JournalEventId ON dbo.AuditLogs;
            ALTER TABLE dbo.AuditLogs DROP COLUMN scan_attempt_id, journal_event_id;
            INSERT dbo.AuditLogs(action_type, affected_entity, affected_entity_id, created_at)
            VALUES ('Legacy', 'POI', 42, '2026-11-05T00:00:00');
            """);

        await database.ExecuteNonQueryAsync(migration);
        await database.ExecuteNonQueryAsync(migration);

        var columns = await ReadColumnInventory(database);
        columns.Should().Contain("scan_attempt_id:uniqueidentifier:16:1");
        columns.Should().Contain("journal_event_id:uniqueidentifier:16:1");
        (await ReadJournalIndexInventory(database)).Should().Be(
            "UX_AuditLogs_JournalEventId:1:[journal_event_id] IS NOT NULL");

        await using (var db = database.CreateDbContext())
        {
            var legacy = await db.AuditLogs.AsNoTracking().SingleAsync();
            legacy.ScanAttemptId.Should().BeNull();
            legacy.JournalEventId.Should().BeNull();
        }

        Guid attemptId = Guid.NewGuid();
        Guid eventId = Guid.NewGuid();
        string insert = $"""
            INSERT dbo.AuditLogs(action_type, affected_entity, created_at, scan_attempt_id, journal_event_id)
            VALUES ('CheckInScanRejected', 'CheckIn', SYSUTCDATETIME(), '{attemptId:D}', '{eventId:D}');
            """;
        await database.ExecuteNonQueryAsync(insert);
        var duplicate = () => database.ExecuteNonQueryAsync(insert);
        (await duplicate.Should().ThrowAsync<SqlException>()).Which.Number.Should().BeOneOf(2601, 2627);

        await database.ExecuteNonQueryAsync("""
            INSERT dbo.AuditLogs(action_type, affected_entity, created_at, scan_attempt_id, journal_event_id)
            VALUES ('LegacyNullOne', 'CheckIn', SYSUTCDATETIME(), NULL, NULL),
                   ('LegacyNullTwo', 'CheckIn', SYSUTCDATETIME(), NULL, NULL);
            """);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CheckInJournalMigration_IncompatibleExistingColumn_RollsBackWithoutPartialSchema()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Database", "migrations", "20261105_add_checkin_audit_journal_identity.sql"));

        await database.ExecuteNonQueryAsync("""
            DROP INDEX UX_AuditLogs_JournalEventId ON dbo.AuditLogs;
            ALTER TABLE dbo.AuditLogs DROP COLUMN scan_attempt_id, journal_event_id;
            ALTER TABLE dbo.AuditLogs ADD scan_attempt_id bigint NULL;
            """);

        var apply = () => database.ExecuteNonQueryAsync(migration);
        await apply.Should().ThrowAsync<SqlException>();
        var columns = await ReadColumnInventory(database);
        columns.Should().Contain("scan_attempt_id:bigint:8:1");
        columns.Should().NotContain(item => item.StartsWith("journal_event_id:", StringComparison.Ordinal));
        (await ReadJournalIndexInventory(database)).Should().BeNull();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Query_OnSqlServer_PreservesInclusiveBoundaryNullActorsExactIdsAndStablePages()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using var db = database.CreateDbContext();
        var logs = new[]
        {
            new AuditLog(null, "Update", "POI", 42, Boundary.AddTicks(-1)),
            new AuditLog(null, "Update", "POI", 42, Boundary),
            new AuditLog(null, "Update", "POI", 142, Boundary),
            new AuditLog(null, "Update", "POI", null, Boundary),
            new AuditLog(null, "Update", "POI", 42, Boundary.AddTicks(1))
        };
        db.AuditLogs.AddRange(logs);
        await db.SaveChangesAsync();
        var handler = new GetAuditLogsQueryHandler(db, new Administrator());
        var boundary = await handler.Handle(new(FromDateUtc: Boundary, ToDateUtc: Boundary), default);
        boundary.IsSuccess.Should().BeTrue();
        boundary.Value.Items.Select(x => x.Id).Should().Equal(logs.Skip(1).Take(3).Select(x => x.Id).OrderDescending());
        boundary.Value.Items.Should().OnlyContain(x => x.ActorFullName == "System" && x.ActorUserId == null);
        var numeric = await handler.Handle(new(Keyword: "42", FromDateUtc: Boundary, ToDateUtc: Boundary), default);
        numeric.Value.Items.Should().ContainSingle().Which.Id.Should().Be(logs[1].Id);
        var first = await handler.Handle(new(FromDateUtc: Boundary, ToDateUtc: Boundary, PageSize: 2), default);
        var second = await handler.Handle(new(FromDateUtc: Boundary, ToDateUtc: Boundary, PageSize: 2, PageNumber: 2), default);
        first.Value.Items.Select(x => x.Id).Concat(second.Value.Items.Select(x => x.Id))
            .Should().Equal(boundary.Value.Items.Select(x => x.Id));
        var text = await handler.Handle(new(Keyword: "Update"), default);
        text.Value.TotalCount.Should().Be(5);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Http_WhenSuccessAuditFails_RollsBackBusinessAndPersistsIndependentFailure()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        User staff;
        int categoryId;
        await using (var db = database.CreateDbContext())
        {
            staff = new User
            {
                Email = "rollback@example.com",
                FullName = "Staff",
                Role = UserRole.Staff,
                Status = AccountStatus.Active,
                CreatedAtUtc = Boundary,
                UpdatedAtUtc = Boundary
            };
            var category = PoiCategory.Create("Rollback test", null);
            db.Users.Add(staff);
            db.PoiCategories.Add(category);
            await db.SaveChangesAsync();
            categoryId = category.Id;
        }
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE dbo.AuditLogs ADD CONSTRAINT CK_Test_RejectSuccess
            CHECK (action_type <> 'POI_CREATE' OR result <> 'Success');
            """);
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer, database.ConnectionString);
        var token = factory.Services.GetRequiredService<IJwtTokenService>().GenerateAccessToken(staff).Token;
        using var client = factory.CreateJwtClient(token);
        var response = await client.PostAsJsonAsync("/api/v1/admin/pois",
            new CreatePoiCommand("Rollback POI", categoryId, 11.94m, 108.43m, "Da Lat", "Rollback test",
                IndoorOutdoorType.Mixed, 60, true));
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await database.ReadPoiIdentityLastValueAsync()).Should().NotBeNull("the business insert must have reached SQL Server");
        await using var verification = database.CreateDbContext();
        (await verification.PointsOfInterest.CountAsync()).Should().Be(0);
        (await verification.PoiOpeningHours.CountAsync()).Should().Be(0);
        (await verification.PoiTags.CountAsync()).Should().Be(0);
        var failure = await verification.AuditLogs.AsNoTracking().SingleAsync();
        failure.Result.Should().Be(AuditOutcome.Failure);
        failure.ActorUserId.Should().Be(staff.Id);
        failure.ActionType.Should().Be("POI_CREATE");
        failure.BeforeData.Should().BeNull();
        using var metadata = JsonDocument.Parse(failure.AfterData!);
        metadata.RootElement.GetProperty("auditMetadata").GetProperty("errorCode").GetString()
            .Should().Be("audit.database_write_failed");
    }

    private static async Task<string[]> ReadColumnInventory(SqlServerTestDatabase database)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.name + ':' + t.name + ':' + CONVERT(varchar(10), c.max_length) + ':' + CONVERT(varchar(1), c.is_nullable)
            FROM sys.columns c JOIN sys.types t ON c.user_type_id = t.user_type_id
            WHERE c.object_id = OBJECT_ID(N'dbo.AuditLogs') ORDER BY c.name;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        return columns.ToArray();
    }

    private static async Task<string?> ReadJournalIndexInventory(SqlServerTestDatabase database)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.name + ':' + CONVERT(varchar(1), i.is_unique) + ':'
                + REPLACE(REPLACE(ISNULL(i.filter_definition, ''), '(', ''), ')', '')
            FROM sys.indexes i
            WHERE i.object_id = OBJECT_ID(N'dbo.AuditLogs')
              AND i.name = N'UX_AuditLogs_JournalEventId';
            """;
        return (string?)await command.ExecuteScalarAsync();
    }

    private sealed class Administrator : ICurrentUserService
    {
        public long? UserId => 1;
        public string? Role => nameof(UserRole.Administrator);
    }
}