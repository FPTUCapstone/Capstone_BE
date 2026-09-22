using System.Text.RegularExpressions;

using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Create;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Scheduling;

[Collection(nameof(TripMateApiFactory))]
public sealed class CreateSchedulingRequestSqlServerTests
{
    private const string RejectGeneratedItineraryConstraint =
        "CK_Itineraries_RejectGeneratedForSchedulingRollback";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CanonicalSchema_AndSchedulingMigrations_ExposeSameSchedulingConstraints()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await ApplySchedulingMigrationsAsync(database);
        await ApplySchedulingMigrationsAsync(database);
        var seed = await SeedAsync(database);

        var schedulingColumns = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'planning.SchedulingRequests')
              AND name IN (
                  N'idempotency_key', N'request_hash', N'start_at', N'time_zone_id',
                  N'end_poi_id', N'return_to_start', N'transport_mode',
                  N'rest_preference', N'failure_code');
            """);
        var hasOperationIndex = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'planning.SchedulingRequests')
              AND name = N'UX_SchedulingRequests_Traveler_Key';
            """);
        var hasEndPoiForeignKey = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID(N'planning.SchedulingRequests')
              AND name = N'FK_SchedulingRequests_EndPoi';
            """);
        var hasItemKindConstraint = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'planning.ItineraryItems')
              AND name = N'CK_ItineraryItems_KindPoi';
            """);

        schedulingColumns.Should().Be(9);
        hasOperationIndex.Should().Be(1);
        hasEndPoiForeignKey.Should().Be(1);
        hasItemKindConstraint.Should().Be(1);
        var writeNegativeCost = () => database.ExecuteNonQueryAsync($"""
            UPDATE catalog.POIs
            SET estimated_visit_cost = -1
            WHERE poi_id = {seed.PoiId};
            """);
        await writeNegativeCost.Should().ThrowAsync<SqlException>();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentSameKey_CreatesOneItineraryAndReplaysOriginalResult()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await ApplySchedulingMigrationsAsync(database);
        var seed = await SeedAsync(database);
        var key = Guid.NewGuid();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<long> CreateAsync()
        {
            await startGate.Task;
            await using var context = database.CreateDbContext();
            var result = await CreateHandler(context).Handle(CreateCommand(seed.UserId, key), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            return result.Value.ItineraryId;
        }

        var first = CreateAsync();
        var second = CreateAsync();
        startGate.SetResult();
        var resultIds = await Task.WhenAll(first, second);

        resultIds[0].Should().Be(resultIds[1]);
        await using var verification = database.CreateDbContext();
        (await verification.SchedulingRequests.CountAsync()).Should().Be(1);
        (await verification.Itineraries.CountAsync()).Should().Be(1);
        (await verification.ItineraryItems.CountAsync()).Should().BeGreaterThan(0);
        var request = await verification.SchedulingRequests.SingleAsync();
        request.Status.Should().Be(SchedulingRequestStatus.Completed);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ItineraryPersistenceFailure_RollsBackRequestItineraryAndItems()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await ApplySchedulingMigrationsAsync(database);
        var seed = await SeedAsync(database);
        await database.ExecuteNonQueryAsync($"""
            ALTER TABLE planning.Itineraries
                ADD CONSTRAINT [{RejectGeneratedItineraryConstraint}]
                CHECK (source_type <> 'CSPGenerated');
            """);

        await using (var context = database.CreateDbContext())
        {
            var action = () => CreateHandler(context).Handle(
                CreateCommand(seed.UserId, Guid.NewGuid()),
                CancellationToken.None);
            await action.Should().ThrowAsync<DbUpdateException>();
        }

        await using var verification = database.CreateDbContext();
        (await verification.SchedulingRequests.CountAsync()).Should().Be(0);
        (await verification.Itineraries.CountAsync()).Should().Be(0);
        (await verification.ItineraryItems.CountAsync()).Should().Be(0);
    }

    private static CreateSchedulingRequestCommandHandler CreateHandler(ApplicationDbContext context) =>
        new(
            context,
            new FixedDateTimeProvider(),
            new FixedRouteDurationProvider(),
            new SqlServerSchedulingRequestLock(context));

    private static async Task<(long UserId, long PoiId)> SeedAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var now = new FixedDateTimeProvider().UtcNow;
        var user = new User
        {
            Email = "scheduling-sql@example.com",
            FullName = "Scheduling SQL Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var category = PoiCategory.Create("Culture", null);
        var poi = PointOfInterest.Create(
            category,
            "Scheduling SQL Museum",
            16.0471m,
            108.2068m,
            user.Id,
            now,
            averageVisitDurationMinutes: 60);
        poi.ConfigurePlanningMetadata(60_000m, "https://example.com/scheduling-sql", now);
        poi.AddOpeningHour(PoiOpeningHour.Create(2, new TimeOnly(7, 0), new TimeOnly(20, 0), false));

        context.PointsOfInterest.Add(poi);
        await context.SaveChangesAsync();
        return (user.Id, poi.Id);
    }

    private static CreateSchedulingRequestCommand CreateCommand(long userId, Guid key) => new(
        userId,
        key,
        new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.FromHours(7)),
        "Asia/Ho_Chi_Minh",
        16.0544m,
        108.2022m,
        16.0471m,
        108.2068m,
        null,
        true,
        480,
        TransportMode.Motorbike,
        10m,
        800_000m,
        [],
        RestPreference.None);

    private static async Task ApplySchedulingMigrationsAsync(SqlServerTestDatabase database)
    {
        foreach (var fileName in new[]
                 {
                     "20260914_add_scheduling_request_generation.sql",
                     "20260915_extend_scheduling_request_contract.sql",
                     "20260919_allow_named_rest_items.sql",
                 })
        {
            var migrationPath = Path.Combine(
                AppContext.BaseDirectory,
                "Database",
                "migrations",
                fileName);
            var migration = await File.ReadAllTextAsync(migrationPath);
            migration = Regex.Replace(migration, @"^\s*GO\s*$", string.Empty, RegexOptions.Multiline);
            await database.ExecuteNonQueryAsync(migration);
        }
    }

    private sealed class FixedDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => new(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
    }

    private sealed class FixedRouteDurationProvider : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            var durations = new int[points.Count, points.Count];
            for (var row = 0; row < points.Count; row++)
            {
                for (var column = 0; column < points.Count; column++)
                {
                    durations[row, column] = row == column ? 0 : 15;
                }
            }

            return Task.FromResult(RouteDurationMatrix.Create(durations));
        }
    }
}