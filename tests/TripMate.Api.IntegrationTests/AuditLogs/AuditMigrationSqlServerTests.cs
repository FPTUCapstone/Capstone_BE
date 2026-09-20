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
        User admin;
        int categoryId;
        await using (var db = database.CreateDbContext())
        {
            admin = new User
            {
                Email = "rollback@example.com",
                FullName = "Administrator",
                Role = UserRole.Administrator,
                Status = AccountStatus.Active,
                CreatedAtUtc = Boundary,
                UpdatedAtUtc = Boundary
            };
            var category = PoiCategory.Create("Rollback test", null);
            db.Users.Add(admin);
            db.PoiCategories.Add(category);
            await db.SaveChangesAsync();
            categoryId = category.Id;
        }
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE dbo.AuditLogs ADD CONSTRAINT CK_Test_RejectSuccess
            CHECK (action_type <> 'POI_CREATE' OR result <> 'Success');
            """);
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer, database.ConnectionString);
        var token = factory.Services.GetRequiredService<IJwtTokenService>().GenerateAccessToken(admin).Token;
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
        failure.ActorUserId.Should().Be(admin.Id);
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

    private sealed class Administrator : ICurrentUserService
    {
        public long? UserId => 1;
        public string? Role => nameof(UserRole.Administrator);
    }
}