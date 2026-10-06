using System.Text.RegularExpressions;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Accept;
using TripMate.Application.Features.Itineraries.AdjustItems;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Itineraries.GetDetail;
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
    public async Task GetDetail_HistoricalIdReturnsCurrentPersistedFriendlyExplanation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedWithHistoricalSuccessorAsync(database);
        await using var context = database.CreateDbContext();

        var result = await new GetItineraryDetailQueryHandler(
                context,
                new ItineraryAccessService(context))
            .Handle(
                new GetItineraryDetailQuery(seed.PredecessorItineraryId, seed.UserId),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ItineraryId.Should().Be(seed.CurrentItineraryId);
        result.Value.Version.Should().Be(2);
        var item = result.Value.Items.Should().ContainSingle().Subject;
        item.RecommendationReason.Should().Be("Current recommendation reason");
        item.FriendlyExplanation.Should().Be("Current persisted explanation");
    }

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
    public async Task ConcurrentRegenerateFromDifferentHistoricalVersionsSerializesOnTheSameSeries()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedWithHistoricalSuccessorAsync(database);
        var barrier = new MutationLockStartBarrier(expectedWaiters: 2);

        async Task<Result<ItineraryDetailResponse>> ExecuteAsync(long itineraryId)
        {
            await using var context = database.CreateDbContext();
            return await CreateRegenerateHandler(context, barrier).Handle(
                new RegenerateItineraryCommand(itineraryId, seed.UserId, Guid.NewGuid()),
                CancellationToken.None);
        }

        var results = await Task.WhenAll(
            ExecuteAsync(seed.PredecessorItineraryId),
            ExecuteAsync(seed.CurrentItineraryId));

        results.Should().OnlyContain(result => result.IsSuccess);
        barrier.AcquiredResources.Should().OnlyContain(resource => resource == seed.SchedulingRequestId);

        await using var verification = database.CreateDbContext();
        var versions = await verification.Itineraries
            .Where(itinerary => itinerary.SchedulingRequestId == seed.SchedulingRequestId)
            .OrderBy(itinerary => itinerary.Version)
            .Select(itinerary => itinerary.Version)
            .ToArrayAsync();
        versions.Should().Equal(1, 2, 3, 4);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentAcceptAndRegenerateShareTheSeriesMutationLock()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var barrier = new MutationLockStartBarrier(expectedWaiters: 2);

        async Task<Result<ItineraryDetailResponse>> AcceptAsync()
        {
            await using var context = database.CreateDbContext();
            return await new AcceptItineraryCommandHandler(
                    context,
                    new ItineraryAccessService(context),
                    new CoordinatedSqlServerItineraryMutationLock(context, barrier),
                    new FixedDateTimeProvider())
                .Handle(
                    new AcceptItineraryCommand(seed.ItineraryId, seed.UserId),
                    CancellationToken.None);
        }

        async Task<Result<ItineraryDetailResponse>> RegenerateAsync()
        {
            await using var context = database.CreateDbContext();
            return await CreateRegenerateHandler(context, barrier).Handle(
                new RegenerateItineraryCommand(seed.ItineraryId, seed.UserId, Guid.NewGuid()),
                CancellationToken.None);
        }

        var results = await Task.WhenAll(AcceptAsync(), RegenerateAsync());

        results.Should().OnlyContain(result => result.IsSuccess);
        barrier.AcquiredResources.Should().OnlyContain(resource => resource == seed.SchedulingRequestId);

        await using var verification = database.CreateDbContext();
        var itineraries = await verification.Itineraries
            .Where(itinerary => itinerary.SchedulingRequestId == seed.SchedulingRequestId)
            .ToArrayAsync();
        itineraries.Should().HaveCount(2);
        itineraries.Count(itinerary => itinerary.Status == Itinerary.ActiveStatus).Should().Be(1);
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
        ApplicationDbContext context,
        MutationLockStartBarrier? barrier = null) =>
        new(
            context,
            new ItineraryAccessService(context),
            new ItineraryVersionService(context, new FixedRouteDurationProvider()),
            barrier is null
                ? new SqlServerItineraryMutationLock(context)
                : new CoordinatedSqlServerItineraryMutationLock(context, barrier),
            new FixedDateTimeProvider());

    private static async Task<HistoricalSeedData> SeedWithHistoricalSuccessorAsync(SqlServerTestDatabase database)
    {
        var seed = await SeedAsync(database);
        await using var context = database.CreateDbContext();
        var schedulingRequestId = await context.Itineraries
            .Where(itinerary => itinerary.Id == seed.ItineraryId)
            .Select(itinerary => itinerary.SchedulingRequestId)
            .SingleAsync();
        var request = await context.SchedulingRequests.SingleAsync(
            item => item.Id == schedulingRequestId!.Value);
        var successor = Itinerary.CreateCspGenerated(
            request,
            "UC11 SQL Trip v2",
            FixedDateTimeProvider.StartAt,
            FixedDateTimeProvider.StartAt.AddHours(4),
            version: 2);
        var successorItem = ItineraryItem.CreateVisit(
            1,
            seed.FirstPoiId,
            FixedDateTimeProvider.StartAt.AddMinutes(30),
            FixedDateTimeProvider.StartAt.AddMinutes(90),
            false,
            50_000m,
            "Current recommendation reason");
        successorItem.AttachFriendlyExplanation("Current persisted explanation");
        successor.AddItem(successorItem);
        context.Itineraries.Add(successor);
        await context.SaveChangesAsync();

        return new HistoricalSeedData(
            seed.UserId,
            seed.ItineraryId,
            successor.Id,
            request.Id);
    }

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
        var generationOwnerId = Guid.NewGuid();
        request.ClaimGeneration(generationOwnerId, now.AddMinutes(1), now);
        request.CompleteGeneration(generationOwnerId, now);
        context.SchedulingRequests.Add(request);
        await context.SaveChangesAsync();

        var itinerary = Itinerary.CreateCspGenerated(
            request,
            "UC11 SQL Trip",
            FixedDateTimeProvider.StartAt,
            FixedDateTimeProvider.StartAt.AddHours(4));
        var firstItem = ItineraryItem.CreateVisit(
            1,
            firstPoi.Id,
            FixedDateTimeProvider.StartAt.AddMinutes(30),
            FixedDateTimeProvider.StartAt.AddMinutes(90),
            false,
            50_000m,
            "Initial visit");
        firstItem.AttachFriendlyExplanation("Historical persisted explanation");
        itinerary.AddItem(firstItem);
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

        return new SeedData(user.Id, itinerary.Id, request.Id, firstPoi.Id, secondPoi.Id);
    }

    private static async Task ApplySchedulingMigrationsAsync(SqlServerTestDatabase database)
    {
        foreach (var fileName in new[]
                 {
                     "20260914_add_scheduling_request_generation.sql",
                    "20260915_extend_scheduling_request_contract.sql",
                    "20260919_allow_named_rest_items.sql",
                    "20261004_add_scheduling_generation_reservation.sql",
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

    private sealed record SeedData(
        long UserId,
        long ItineraryId,
        long SchedulingRequestId,
        long FirstPoiId,
        long SecondPoiId);

    private sealed record HistoricalSeedData(
        long UserId,
        long PredecessorItineraryId,
        long CurrentItineraryId,
        long SchedulingRequestId);

    private sealed class MutationLockStartBarrier(int expectedWaiters)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _waiterCount;

        public List<long> AcquiredResources { get; } = [];

        public Task WaitAsync(long resource, CancellationToken cancellationToken)
        {
            AcquiredResources.Add(resource);
            if (Interlocked.Increment(ref _waiterCount) == expectedWaiters)
            {
                _release.TrySetResult();
            }

            return _release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class CoordinatedSqlServerItineraryMutationLock(
        ApplicationDbContext dbContext,
        MutationLockStartBarrier barrier)
        : IItineraryMutationLock
    {
        private readonly SqlServerItineraryMutationLock _inner = new(dbContext);

        public async Task AcquireAsync(long mutationResourceId, CancellationToken cancellationToken)
        {
            await barrier.WaitAsync(mutationResourceId, cancellationToken);
            await _inner.AcquireAsync(mutationResourceId, cancellationToken);
        }
    }

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