using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.PointsOfInterest.Create;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.PointsOfInterest;

[Collection(nameof(TripMateApiFactory))]
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
        var before = await ReadCountsAsync(database);

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
        var tagMappings = await verificationContext.PoiTags
            .AsNoTracking()
            .Where(item => item.PointOfInterestId == poiId)
            .OrderBy(item => item.TagId)
            .ToListAsync();
        var tagIds = tagMappings.Select(mapping => mapping.TagId).ToArray();
        var audit = await verificationContext.AuditLogs
            .AsNoTracking()
            .SingleAsync(item => item.AffectedEntityId == poiId);

        poi.Name.Should().Be("SQL Success POI");
        poi.Address.Should().Be("Da Lat, Lam Dong");
        poi.Description.Should().Be("SQL Server transaction integration test");
        poi.CategoryId.Should().Be(seed.CategoryId);
        poi.CreatedById.Should().Be(seed.UserId);
        poi.Latitude.Should().Be(11.941755m);
        poi.Longitude.Should().Be(108.438278m);
        poi.IndoorOutdoor.Should().Be(IndoorOutdoorType.Mixed);
        poi.AverageVisitDurationMinutes.Should().Be(90);
        poi.HasShelter.Should().BeTrue();
        poi.Status.Should().Be(PointOfInterestStatus.Active);
        poi.ScenicScore.Should().BeNull();
        poi.PhotoRating.Should().BeNull();
        poi.CreatedAtUtc.Should().Be(TestTime);
        poi.UpdatedAtUtc.Should().Be(TestTime);

        openingHours.Should().HaveCount(2);
        openingHours[0].PointOfInterestId.Should().Be(poiId);
        openingHours[0].DayOfWeek.Should().Be(0);
        openingHours[0].OpenTime.Should().BeNull();
        openingHours[0].CloseTime.Should().BeNull();
        openingHours[0].IsClosed.Should().BeTrue();
        openingHours[1].PointOfInterestId.Should().Be(poiId);
        openingHours[1].DayOfWeek.Should().Be(1);
        openingHours[1].OpenTime.Should().Be(new TimeOnly(8, 0));
        openingHours[1].CloseTime.Should().Be(new TimeOnly(17, 0));
        openingHours[1].IsClosed.Should().BeFalse();

        tagMappings.Should().HaveCount(seed.TagIds.Length);
        tagMappings.Should().OnlyContain(mapping => mapping.PointOfInterestId == poiId);
        tagIds.Should().Equal(seed.TagIds.OrderBy(id => id));

        audit.Id.Should().BePositive();
        audit.ActorUserId.Should().Be(seed.UserId);
        audit.ActionType.Should().Be(AuditActionTypes.PoiCreate);
        audit.AffectedEntity.Should().Be(AuditEntityTypes.PointOfInterest);
        audit.AffectedEntityId.Should().Be(poiId);
        audit.BeforeData.Should().BeNull();
        audit.AfterData.Should().NotBeNullOrWhiteSpace();
        audit.IpAddress.Should().BeNull();
        audit.CreatedAtUtc.Should().Be(TestTime);

        using var auditJson = JsonDocument.Parse(audit.AfterData!);
        var payload = auditJson.RootElement;
        payload.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
        [
            "id",
            "categoryId",
            "name",
            "description",
            "latitude",
            "longitude",
            "address",
            "indoorOutdoor",
            "scenicScore",
            "photoRating",
            "averageVisitDurationMinutes",
            "hasShelter",
            "status",
            "createdById",
            "createdAtUtc",
            "updatedAtUtc",
            "openingHours",
            "tagIds",
        ]);
        payload.GetProperty("id").GetInt64().Should().Be(poi.Id);
        payload.GetProperty("categoryId").GetInt32().Should().Be(poi.CategoryId);
        payload.GetProperty("name").GetString().Should().Be(poi.Name);
        payload.GetProperty("description").GetString().Should().Be(poi.Description);
        payload.GetProperty("latitude").GetDecimal().Should().Be(poi.Latitude);
        payload.GetProperty("longitude").GetDecimal().Should().Be(poi.Longitude);
        payload.GetProperty("address").GetString().Should().Be(poi.Address);
        payload.GetProperty("indoorOutdoor").GetString()
            .Should().Be(nameof(IndoorOutdoorType.Mixed));
        payload.GetProperty("scenicScore").ValueKind.Should().Be(JsonValueKind.Null);
        payload.GetProperty("photoRating").ValueKind.Should().Be(JsonValueKind.Null);
        payload.GetProperty("averageVisitDurationMinutes").GetInt32()
            .Should().Be(poi.AverageVisitDurationMinutes);
        payload.GetProperty("hasShelter").GetBoolean().Should().Be(poi.HasShelter);
        payload.GetProperty("status").GetString()
            .Should().Be(nameof(PointOfInterestStatus.Active));
        payload.GetProperty("createdById").GetInt64().Should().Be(poi.CreatedById);
        payload.GetProperty("createdAtUtc").GetDateTimeOffset().Should().Be(poi.CreatedAtUtc);
        payload.GetProperty("updatedAtUtc").GetDateTimeOffset().Should().Be(poi.UpdatedAtUtc);

        var auditOpeningHours = payload.GetProperty("openingHours").EnumerateArray().ToArray();
        auditOpeningHours.Should().HaveCount(2);
        AssertAuditOpeningHour(auditOpeningHours[0], openingHours[0]);
        AssertAuditOpeningHour(auditOpeningHours[1], openingHours[1]);
        payload.GetProperty("tagIds").EnumerateArray().Select(item => item.GetInt32())
            .Should().Equal(tagIds);

        var after = await ReadCountsAsync(database);
        after.Should().Be(before with
        {
            Pois = before.Pois + 1,
            OpeningHours = before.OpeningHours + 2,
            PoiTags = before.PoiTags + seed.TagIds.Length,
            AuditLogs = before.AuditLogs + 1,
        });
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

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Post_WhenAuditInsertFails_ReturnsSanitizedProblemAndRollsBackAggregate()
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

        await using var factory = new TripMateApiFactory(
            ApiTestAuthenticationMode.JwtBearer,
            database.ConnectionString);
        User administrator;
        await using (var context = database.CreateDbContext())
        {
            administrator = await context.Users
                .AsNoTracking()
                .SingleAsync(user => user.Id == seed.UserId);
        }

        var tokenService = factory.Services.GetRequiredService<IJwtTokenService>();
        var token = tokenService.GenerateAccessToken(administrator).Token;
        using var client = factory.CreateJwtClient(token);

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/pois",
            CreateCommand(seed, "SQL HTTP Rollback POI"));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType
            .Should().Be("application/problem+json");
        var responseBody = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(responseBody);
        problem.RootElement.GetProperty("status").GetInt32()
            .Should().Be((int)HttpStatusCode.InternalServerError);
        problem.RootElement.GetProperty("title").GetString()
            .Should().Be("An unexpected error occurred.");
        problem.RootElement.TryGetProperty("detail", out _).Should().BeFalse();
        responseBody.Should().NotContain(RejectAuditConstraintName);
        responseBody.Should().NotContain("SqlException");
        responseBody.ToLowerInvariant().Should().NotContain("stack");

        var identityAfter = await database.ReadPoiIdentityLastValueAsync();
        identityAfter.Should().NotBeNull(
            "the HTTP request must reach the first POI save before the audit insert fails");
        identityAfter!.Value.Should().BeGreaterThan(identityBefore ?? 0);

        var after = await ReadCountsAsync(database);
        after.Should().Be(
            before,
            "the HTTP request transaction must roll back the POI, child rows, and audit row");
    }

    private static void AssertAuditOpeningHour(
        JsonElement payload,
        PoiOpeningHour persisted)
    {
        payload.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            ["dayOfWeek", "openTime", "closeTime", "isClosed"]);
        payload.GetProperty("dayOfWeek").GetInt32().Should().Be(persisted.DayOfWeek);

        var openTime = payload.GetProperty("openTime");
        var closeTime = payload.GetProperty("closeTime");
        if (persisted.IsClosed)
        {
            openTime.ValueKind.Should().Be(JsonValueKind.Null);
            closeTime.ValueKind.Should().Be(JsonValueKind.Null);
        }
        else
        {
            TimeOnly.Parse(openTime.GetString()!).Should().Be(persisted.OpenTime);
            TimeOnly.Parse(closeTime.GetString()!).Should().Be(persisted.CloseTime);
        }

        payload.GetProperty("isClosed").GetBoolean().Should().Be(persisted.IsClosed);
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