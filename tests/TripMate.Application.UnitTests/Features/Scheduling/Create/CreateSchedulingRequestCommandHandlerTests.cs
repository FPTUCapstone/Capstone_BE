using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Create;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Create;

public sealed class CreateSchedulingRequestCommandHandlerTests
{
    private readonly FakeDateTimeProvider _clock = new()
    {
        UtcNow = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task Handle_SameKeyAndPayload_ReplaysOriginalItinerary()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        await SeedSelectablePoiAsync(dbContext, "Dragon Bridge", 16.0615m, 108.2277m);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid());

        var first = await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(command, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.ItineraryId.Should().Be(first.Value.ItineraryId);
        first.Value.Items.Select(item => item.TravelDurationToNextMinutes)
            .Should().Contain(duration => duration.HasValue);
        replay.Value.Items.Select(item => item.TravelDurationToNextMinutes)
            .Should().Equal(first.Value.Items.Select(item => item.TravelDurationToNextMinutes));
        (await dbContext.Itineraries.CountAsync()).Should().Be(1);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_InfeasibleEndPoi_ReplaysStoredFailureForSameKey()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid()) with
        {
            EndPoiId = 999_999,
            ReturnToStart = false,
        };

        var first = await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(command, CancellationToken.None);

        first.IsFailure.Should().BeTrue();
        first.ErrorCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        replay.IsFailure.Should().BeTrue();
        replay.ErrorCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(1);
        var persisted = await dbContext.SchedulingRequests.SingleAsync();
        persisted.Status.Should().Be(SchedulingRequestStatus.Failed);
        persisted.FailureCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        (await dbContext.Itineraries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_MandatoryPoiOutsideSearchArea_ReplaysStoredFailureForSameKey()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var outsidePoi = await SeedSelectablePoiAsync(dbContext, "Outside area", 16.3000m, 108.3000m);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid()) with
        {
            MandatoryPoiIds = [outsidePoi.Id],
        };

        await AssertInfeasibleReplayAsync(handler, command, dbContext);
    }

    [Fact]
    public async Task Handle_NoSelectablePoi_ReplaysStoredFailureForSameKey()
    {
        await using var dbContext = TestDbContext.Create();
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid());

        await AssertInfeasibleReplayAsync(handler, command, dbContext);
    }

    [Fact]
    public async Task Handle_PublicTransit_ReplaysStoredFailureForSameKey()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid()) with
        {
            TransportMode = TransportMode.PublicTransit,
        };

        await AssertInfeasibleReplayAsync(handler, command, dbContext);
    }

    [Fact]
    public async Task Handle_SameKeyWithDifferentPayload_ReturnsConflict()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid());

        await handler.Handle(command, CancellationToken.None);
        var mismatch = await handler.Handle(
            command with { AvailableMinutes = 420 },
            CancellationToken.None);

        mismatch.IsFailure.Should().BeTrue();
        mismatch.ErrorCode.Should().Be(SchedulingErrorCodes.IdempotencyKeyPayloadMismatch);
        (await dbContext.Itineraries.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_EquivalentNormalizedCoordinates_ReplaysInsteadOfReturningConflict()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid());
        var equivalent = command with
        {
            StartLatitude = 16.0544004m,
            StartLongitude = 108.2022004m,
            ExplorationLatitude = 16.0471004m,
            ExplorationLongitude = 108.2068004m,
            SearchRadiusKm = 10.0004m,
            BudgetVnd = 800_000.0004m,
        };

        var first = await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(equivalent, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.ItineraryId.Should().Be(first.Value.ItineraryId);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_FourthSuccessfulRequestOnSameLocalDate_IsAllowed()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var handler = CreateHandler(dbContext);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = await handler.Handle(CreateCommand(Guid.NewGuid()), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
        }

        var fourth = await handler.Handle(CreateCommand(Guid.NewGuid()), CancellationToken.None);

        fourth.IsSuccess.Should().BeTrue();
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(4);
    }

    [Fact]
    public async Task Handle_UsesSavedTravelerInterestTagsToRankOptionalPois()
    {
        await using var dbContext = TestDbContext.Create();
        var preferredCategory = PoiCategory.Create("Culture", null);
        var otherCategory = PoiCategory.Create("Nature", null);
        var preferredPoi = PointOfInterest.Create(
            preferredCategory,
            "Preferred museum",
            16.0471m,
            108.2068m,
            1,
            _clock.UtcNow,
            averageVisitDurationMinutes: 60);
        preferredPoi.ConfigurePlanningMetadata(60_000m, "https://example.com/preferred", _clock.UtcNow);
        preferredPoi.AddOpeningHour(PoiOpeningHour.Create(2, new TimeOnly(7, 0), new TimeOnly(20, 0), false));
        var otherPoi = PointOfInterest.Create(
            otherCategory,
            "Other attraction",
            16.0472m,
            108.2069m,
            1,
            _clock.UtcNow,
            averageVisitDurationMinutes: 60);
        otherPoi.ConfigurePlanningMetadata(60_000m, "https://example.com/other", _clock.UtcNow);
        otherPoi.AddOpeningHour(PoiOpeningHour.Create(2, new TimeOnly(7, 0), new TimeOnly(20, 0), false));
        dbContext.PointsOfInterest.AddRange(otherPoi, preferredPoi);
        dbContext.TravelerProfiles.Add(TravelerProfile.Create(
            42,
            "[\"culture\"]",
            _clock.UtcNow));
        await dbContext.SaveChangesAsync();

        var result = await CreateHandler(dbContext).Handle(
            CreateCommand(Guid.NewGuid()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items
            .Where(item => item.ItemKind == ItineraryItemKind.Visit)
            .Select(item => item.PoiId)
            .Should().StartWith(preferredPoi.Id);
    }

    [Fact]
    public async Task Handle_MoreThanFortyEligiblePois_BoundsMatrixAndRetainsMandatoryPois()
    {
        await using var dbContext = TestDbContext.Create();
        var pois = new List<PointOfInterest>();
        for (var index = 0; index < 45; index++)
        {
            pois.Add(await SeedSelectablePoiAsync(
                dbContext,
                $"Candidate {index:D2}",
                16.0471m + (index * 0.0001m),
                108.2068m + (index * 0.0001m)));
        }

        var routeProvider = new RecordingRouteDurationProvider();
        var command = CreateCommand(Guid.NewGuid()) with
        {
            MandatoryPoiIds = [pois[^2].Id, pois[^1].Id],
        };

        var result = await CreateHandler(dbContext, routeProvider).Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        routeProvider.LastPointCount.Should().BeLessOrEqualTo(42);
        result.Value.Items.Select(item => item.PoiId)
            .Should().Contain([pois[^2].Id, pois[^1].Id]);
    }

    private CreateSchedulingRequestCommandHandler CreateHandler(
        TestDbContext dbContext,
        IRouteDurationProvider? routeDurationProvider = null) =>
        new(
            dbContext,
            _clock,
            routeDurationProvider ?? new FixedRouteDurationProvider(),
            new NoOpSchedulingRequestLock());

    private static Task<PointOfInterest> SeedSelectablePoiAsync(TestDbContext dbContext) =>
        SeedSelectablePoiAsync(dbContext, "Cham Museum", 16.0471m, 108.2068m);

    private static async Task<PointOfInterest> SeedSelectablePoiAsync(
        TestDbContext dbContext,
        string name,
        decimal latitude,
        decimal longitude)
    {
        var category = PoiCategory.Create("Culture", null);
        var poi = PointOfInterest.Create(
            category,
            name,
            latitude,
            longitude,
            1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            averageVisitDurationMinutes: 60);
        poi.ConfigurePlanningMetadata(60_000m, "https://example.com/cham", DateTimeOffset.UtcNow);
        poi.AddOpeningHour(PoiOpeningHour.Create(2, new TimeOnly(7, 0), new TimeOnly(20, 0), false));
        dbContext.PointsOfInterest.Add(poi);
        await dbContext.SaveChangesAsync();
        return poi;
    }

    private static CreateSchedulingRequestCommand CreateCommand(Guid key) => new(
        TravelerUserId: 42,
        IdempotencyKey: key,
        StartAt: new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.FromHours(7)),
        TimeZoneId: "Asia/Ho_Chi_Minh",
        StartLatitude: 16.0544m,
        StartLongitude: 108.2022m,
        ExplorationLatitude: 16.0471m,
        ExplorationLongitude: 108.2068m,
        EndPoiId: null,
        ReturnToStart: true,
        AvailableMinutes: 480,
        TransportMode: TransportMode.Motorbike,
        SearchRadiusKm: 10m,
        BudgetVnd: 800_000m,
        MandatoryPoiIds: [],
        RestPreference: RestPreference.None);

    private static async Task AssertInfeasibleReplayAsync(
        CreateSchedulingRequestCommandHandler handler,
        CreateSchedulingRequestCommand command,
        TestDbContext dbContext)
    {
        var first = await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(command, CancellationToken.None);

        first.IsFailure.Should().BeTrue();
        first.ErrorCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        replay.IsFailure.Should().BeTrue();
        replay.ErrorCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(1);
        (await dbContext.SchedulingRequests.SingleAsync()).Status.Should().Be(SchedulingRequestStatus.Failed);
        (await dbContext.Itineraries.CountAsync()).Should().Be(0);
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

    private sealed class RecordingRouteDurationProvider : IRouteDurationProvider
    {
        public int LastPointCount { get; private set; }

        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            LastPointCount = points.Count;
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

    private sealed class NoOpSchedulingRequestLock : ISchedulingRequestLock
    {
        public Task AcquireAsync(
            long travelerUserId,
            Guid idempotencyKey,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}