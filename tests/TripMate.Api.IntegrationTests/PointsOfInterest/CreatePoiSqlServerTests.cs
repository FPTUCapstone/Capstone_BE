using System.Text.Json;

using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.PointsOfInterest.Create;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.PointsOfInterest;

public sealed class CreatePoiSqlServerTests
{
    private const string RejectAuditConstraintName =
        "CK_AuditLogs_RejectPoiCreateForRollbackTest";
    private static readonly DateTimeOffset TestTime =
        new(2026, 9, 10, 1, 30, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_WithCompletePoi_PersistsAggregateAndAuditOnSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedReferencesAsync(database);

        long poiId;
        await using (var context = database.CreateDbContext())
        {
            var handler = CreateHandler(context, seed.UserId);

            var result = await handler.Handle(CreateCommand(seed, "SQL Success POI"), default);

            result.IsSuccess.Should().BeTrue();
            result.Value.Id.Should().BePositive();
            poiId = result.Value.Id;
        }

        await using var verificationContext = database.CreateDbContext();
        var poi = await verificationContext.PointsOfInterest
            .AsNoTracking()
            .SingleAsync(item => item.Id == poiId);
        var openingHours = await verificationContext.PoiOpeningHours
            .AsNoTracking()
            .Where(item => item.PointOfInterestId == poiId)
            .OrderBy(item => item.DayOfWeek)
            .ToListAsync();
        var tagIds = await verificationContext.PoiTags
            .AsNoTracking()
            .Where(item => item.PointOfInterestId == poiId)
            .Select(item => item.TagId)
            .OrderBy(id => id)
            .ToListAsync();
        var audit = await verificationContext.AuditLogs
            .AsNoTracking()
            .SingleAsync(item => item.AffectedEntityId == poiId);

        poi.Name.Should().Be("SQL Success POI");
        poi.CategoryId.Should().Be(seed.CategoryId);
        poi.CreatedById.Should().Be(seed.UserId);
        openingHours.Select(item => item.DayOfWeek).Should().Equal(0, 1);
        tagIds.Should().Equal(seed.TagIds.OrderBy(id => id));
        audit.ActorUserId.Should().Be(seed.UserId);
        audit.ActionType.Should().Be(AuditActionTypes.PoiCreate);
        audit.AffectedEntity.Should().Be(AuditEntityTypes.PointOfInterest);

        using var auditJson = JsonDocument.Parse(audit.AfterData!);
        auditJson.RootElement.GetProperty("id").GetInt64().Should().Be(poiId);
        auditJson.RootElement.GetProperty("categoryId").GetInt32().Should().Be(poi.CategoryId);
        auditJson.RootElement.GetProperty("name").GetString().Should().Be(poi.Name);
        auditJson.RootElement.GetProperty("latitude").GetDecimal().Should().Be(poi.Latitude);
        auditJson.RootElement.GetProperty("longitude").GetDecimal().Should().Be(poi.Longitude);
        auditJson.RootElement.GetProperty("createdById").GetInt64().Should().Be(seed.UserId);
        auditJson.RootElement.GetProperty("tagIds")
            .EnumerateArray()
            .Select(element => element.GetInt32())
            .Should().Equal(tagIds);

        var auditOpeningHours = auditJson.RootElement.GetProperty("openingHours")
            .EnumerateArray()
            .ToArray();
        auditOpeningHours.Select(element => element.GetProperty("dayOfWeek").GetInt32())
            .Should().Equal(0, 1);
        auditOpeningHours.Select(element => element.GetProperty("isClosed").GetBoolean())
            .Should().Equal(true, false);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_WhenAuditInsertFails_RollsBackPoiAggregateOnSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedReferencesAsync(database);
        var before = await ReadCountsAsync(database);
        var identityBefore = await database.ReadPoiIdentityLastValueAsync();

        await database.ExecuteNonQueryAsync($"""
            ALTER TABLE dbo.AuditLogs
            ADD CONSTRAINT [{RejectAuditConstraintName}]
            CHECK (actor_user_id <> {seed.UserId} OR action_type <> 'POI_CREATE');
            """);

        await using (var context = database.CreateDbContext())
        {
            var handler = CreateHandler(context, seed.UserId);
            var action = () => handler.Handle(
                CreateCommand(seed, "SQL Rollback POI"),
                default);

            var exception = await action.Should().ThrowAsync<DbUpdateException>();
            var sqlException = exception.Which.InnerException
                .Should().BeOfType<SqlException>().Subject;
            sqlException.Number.Should().Be(547);
            sqlException.Message.Should().Contain(RejectAuditConstraintName);
        }

        var identityAfter = await database.ReadPoiIdentityLastValueAsync();
        identityAfter.Should().NotBeNull(
            "the first POI save must reach SQL Server before the audit save fails");
        identityAfter!.Value.Should().BeGreaterThan(
            identityBefore ?? 0,
            "SQL Server must allocate a POI identity before the second save is rejected");

        var after = await ReadCountsAsync(database);
        after.Should().Be(
            before,
            "disposing the failed physical transaction must roll back the POI and every child row");
    }

    private static CreatePoiCommandHandler CreateHandler(
        ApplicationDbContext context,
        long userId) =>
        new(
            context,
            new TestCurrentUserService(userId),
            new TestDateTimeProvider(TestTime));

    private static CreatePoiCommand CreateCommand(SeedResult seed, string name) =>
        new(
            name,
            seed.CategoryId,
            11.941755m,
            108.438278m,
            "Da Lat, Lam Dong",
            "SQL Server transaction integration test",
            IndoorOutdoorType.Mixed,
            90,
            true,
            [
                new CreatePoiOpeningHourInput(0, null, null, true),
                new CreatePoiOpeningHourInput(
                    1,
                    new TimeOnly(8, 0),
                    new TimeOnly(17, 0),
                    false),
            ],
            seed.TagIds);

    private static async Task<SeedResult> SeedReferencesAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var user = new User
        {
            Email = "sql-integration-test@example.com",
            FullName = "SQL Integration Test Administrator",
            Role = UserRole.Administrator,
            Status = AccountStatus.Active,
            CreatedAtUtc = TestTime,
            UpdatedAtUtc = TestTime,
        };
        var category = PoiCategory.Create("SQL Test Category", null);
        var tags = new[]
        {
            Tag.Create("SQL Test Nature"),
            Tag.Create("SQL Test Family"),
        };

        context.Users.Add(user);
        context.PoiCategories.Add(category);
        context.Tags.AddRange(tags);
        await context.SaveChangesAsync();

        return new SeedResult(
            user.Id,
            category.Id,
            tags.Select(tag => tag.Id).ToArray());
    }

    private static async Task<DatabaseCounts> ReadCountsAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();

        return new DatabaseCounts(
            await context.Users.CountAsync(),
            await context.PoiCategories.CountAsync(),
            await context.Tags.CountAsync(),
            await context.PointsOfInterest.CountAsync(),
            await context.PoiOpeningHours.CountAsync(),
            await context.PoiTags.CountAsync(),
            await context.AuditLogs.CountAsync());
    }

    private sealed record SeedResult(long UserId, int CategoryId, int[] TagIds);

    private sealed record DatabaseCounts(
        int Users,
        int Categories,
        int Tags,
        int Pois,
        int OpeningHours,
        int PoiTags,
        int AuditLogs);

    private sealed class TestCurrentUserService(long userId) : ICurrentUserService
    {
        public long? UserId { get; } = userId;

        public string? Role { get; } = nameof(UserRole.Administrator);
    }

    private sealed class TestDateTimeProvider(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}