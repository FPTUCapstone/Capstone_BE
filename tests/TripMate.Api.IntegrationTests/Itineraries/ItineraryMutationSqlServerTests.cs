using System.Text.RegularExpressions;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.AdjustItems;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Itineraries.Regenerate;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Itineraries;

[Collection(nameof(TripMateApiFactory))]
public sealed class ItineraryMutationSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Regenerate_SameKeyReplaysTheSameSuccessor()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var key = Guid.NewGuid();

        var first = await RunRegenerateAsync(database, seed, key);
        var replay = await RunRegenerateAsync(database, seed, key);

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.ItineraryId.Should().Be(first.Value.ItineraryId);

        await using var verification = database.CreateDbContext();
        (await verification.Itineraries.CountAsync()).Should().Be(2);
        (await verification.ItineraryVersionOperations.CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Adjust_SameKeyWithDifferentPayloadIsRejected()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var key = Guid.NewGuid();

        var first = await RunAdjustAsync(database, seed, key, [seed.FirstPoiId]);
        var mismatch = await RunAdjustAsync(database, seed, key, [seed.SecondPoiId]);

        first.IsSuccess.Should().BeTrue();
        mismatch.IsFailure.Should().BeTrue();
        mismatch.ErrorCode.Should().Be(ItineraryErrorCodes.IdempotencyKeyPayloadMismatch);

        await using var verification = database.CreateDbContext();
        (await verification.Itineraries.CountAsync()).Should().Be(2);
        (await verification.ItineraryVersionOperations.CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentRegenerateWithSameKeyCreatesOneSuccessorAndReplaysIt()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var key = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Result<ItineraryDetailResponse>> ExecuteAsync()
        {
            await gate.Task;
            await using var context = database.CreateDbContext();
            return await CreateRegenerateHandler(context).Handle(
                new RegenerateItineraryCommand(seed.ItineraryId, seed.UserId, key),
                CancellationToken.None);
        }

        var first = ExecuteAsync();
        var second = ExecuteAsync();
        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        results.Should().OnlyContain(result => result.IsSuccess);
        results[0].Value.ItineraryId.Should().Be(results[1].Value.ItineraryId);

        await using var verification = database.CreateDbContext();
        (await verification.Itineraries.CountAsync()).Should().Be(2);
        (await verification.ItineraryVersionOperations.CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentRegenerateWithDifferentKeysCreatesTwoSuccessors()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Result<ItineraryDetailResponse>> ExecuteAsync(Guid key)
        {
            await gate.Task;
            await using var context = database.CreateDbContext();
            return await CreateRegenerateHandler(context).Handle(
                new RegenerateItineraryCommand(seed.ItineraryId, seed.UserId, key),
                CancellationToken.None);
        }

        var first = ExecuteAsync(Guid.NewGuid());
        var second = ExecuteAsync(Guid.NewGuid());
        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        results.Should().OnlyContain(result => result.IsSuccess);
        results.Select(result => result.Value.ItineraryId).Distinct().Should().HaveCount(2);

        await using var verification = database.CreateDbContext();
        (await verification.Itineraries.CountAsync()).Should().Be(3);
        (await verification.ItineraryVersionOperations.CountAsync()).Should().Be(2);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Regenerate_WhenOperationPersistenceFailsRollsBackSuccessorAndOperation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE planning.ItineraryVersionOperations
                ADD CONSTRAINT CK_ItineraryVersionOperations_RejectRegenerate
                CHECK (operation_type <> 'Regenerate');
            """);

        await using (var context = database.CreateDbContext())
        {
            var action = () => CreateRegenerateHandler(context).Handle(
                new RegenerateItineraryCommand(seed.ItineraryId, seed.UserId, Guid.NewGuid()),
                CancellationToken.None);
            await action.Should().ThrowAsync<DbUpdateException>();
        }

        await using var verification = database.CreateDbContext();
        (await verification.Itineraries.CountAsync()).Should().Be(1);
        (await verification.ItineraryVersionOperations.CountAsync()).Should().Be(0);
    }

    private static async Task<Result<ItineraryDetailResponse>> RunRegenerateAsync(
        SqlServerTestDatabase database,
        SeedData seed,
        Guid key)
    {
        await using var context = database.CreateDbContext();
        return await CreateRegenerateHandler(context).Handle(
            new RegenerateItineraryCommand(seed.ItineraryId, seed.UserId, key),
            CancellationToken.None);
    }

    private static async Task<Result<ItineraryDetailResponse>> RunAdjustAsync(
        SqlServerTestDatabase database,
        SeedData seed,
        Guid key,
        IReadOnlyCollection<long> poiIds)
    {
        await using var context = database.CreateDbContext();
        return await new AdjustItineraryItemsCommandHandler(
                context,
                new ItineraryAccessService(context),
                new ItineraryVersionService(context, new FixedRouteDurationProvider()),
                new SqlServerItineraryMutationLock(context),
                new FixedDateTimeProvider())
            .Handle(
                new AdjustItineraryItemsCommand(
                    seed.ItineraryId,
                    seed.UserId,
                    key,
                    poiIds),
                CancellationToken.None);
    }

    private static RegenerateItineraryCommandHandler CreateRegenerateHandler(
        ApplicationDbContext context) =>
        new(
            context,
            new ItineraryAccessService(context),
            new ItineraryVersionService(context, new FixedRouteDurationProvider()),
            new SqlServerItineraryMutationLock(context),
            new FixedDateTimeProvider());

    private static async Task<SeedData> SeedAsync(SqlServerTestDatabase database)
    {
        await ApplySchedulingMigrationsAsync(database);
        await using var context = database.CreateDbContext();
        var now = FixedDateTimeProvider.UtcNow;
        var user = new User
        {
            Email = $"uc11-sql-{Guid.NewGuid():N}@example.com",
            FullName = "UC11 SQL Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var category = PoiCategory.Create("Culture", null);
        var firstPoi = CreatePoi(category, user.Id, "UC11 SQL Museum", now);
        var secondPoi = CreatePoi(category, user.Id, "UC11 SQL Gallery", now);
        context.PointsOfInterest.AddRange(firstPoi, secondPoi);
        await context.SaveChangesAsync();

        var request = SchedulingRequest.Create(
            user.Id,
            Guid.NewGuid(),
            new string('a', SchedulingRequest.RequestHashLength),
            FixedDateTimeProvider.StartAt,
            "Asia/Ho_Chi_Minh",
            16.0471m,
            108.2068m,
            16.0471m,
            108.2068m,
            null,
            true,
            480,
            TransportMode.Motorbike,
            10m,
            800_000m,
            "[]",
            RestPreference.None,
            now);
        request.Complete(now);
        context.SchedulingRequests.Add(request);
        await context.SaveChangesAsync();

        var itinerary = Itinerary.CreateCspGenerated(
            request,
            "UC11 SQL Trip",
            FixedDateTimeProvider.StartAt,
            FixedDateTimeProvider.StartAt.AddHours(4));
        itinerary.AddItem(ItineraryItem.CreateVisit(
            1,
            firstPoi.Id,
            FixedDateTimeProvider.StartAt.AddMinutes(30),
            FixedDateTimeProvider.StartAt.AddMinutes(90),
            false,
            50_000m,
            "Initial visit"));
        itinerary.AddItem(ItineraryItem.CreateVisit(
            2,
            secondPoi.Id,
            FixedDateTimeProvider.StartAt.AddMinutes(120),
            FixedDateTimeProvider.StartAt.AddMinutes(180),
            false,
            50_000m,
            "Initial visit"));
        context.Itineraries.Add(itinerary);
        await context.SaveChangesAsync();

        return new SeedData(user.Id, itinerary.Id, firstPoi.Id, secondPoi.Id);
    }

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

    private static PointOfInterest CreatePoi(
        PoiCategory category,
        long userId,
        string name,
        DateTimeOffset now)
    {
        var poi = PointOfInterest.Create(
            category,
            name,
            16.0471m,
            108.2068m,
            userId,
            now,
            averageVisitDurationMinutes: 60);
        poi.ConfigurePlanningMetadata(50_000m, "https://example.com/uc11", now);
        poi.AddOpeningHour(PoiOpeningHour.Create(2, new TimeOnly(7, 0), new TimeOnly(20, 0), false));
        return poi;
    }

    private sealed record SeedData(long UserId, long ItineraryId, long FirstPoiId, long SecondPoiId);

    private sealed class FixedDateTimeProvider : IDateTimeProvider
    {
        public static DateTimeOffset UtcNow => new(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);

        public static DateTimeOffset StartAt => UtcNow.AddHours(1);

        DateTimeOffset IDateTimeProvider.UtcNow => UtcNow;
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
                    durations[row, column] = row == column ? 0 : 10;
                }
            }

            return Task.FromResult(RouteDurationMatrix.Create(durations));
        }
    }
}