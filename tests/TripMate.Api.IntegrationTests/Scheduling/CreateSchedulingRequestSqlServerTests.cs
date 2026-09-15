using System.Text.RegularExpressions;

using FluentAssertions;

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
