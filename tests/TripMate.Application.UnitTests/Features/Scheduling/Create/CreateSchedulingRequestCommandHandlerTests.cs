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
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid());

        var first = await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(command, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.ItineraryId.Should().Be(first.Value.ItineraryId);
        (await dbContext.Itineraries.CountAsync()).Should().Be(1);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(1);
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
            .Select(item => item.PointOfInterestId)
            .Should().StartWith(preferredPoi.Id);
    }

    private CreateSchedulingRequestCommandHandler CreateHandler(TestDbContext dbContext) =>
        new(
            dbContext,
            _clock,
            new FixedRouteDurationProvider(),
            new NoOpSchedulingRequestLock());

    private static async Task SeedSelectablePoiAsync(TestDbContext dbContext)
    {
        var category = PoiCategory.Create("Culture", null);
        var poi = PointOfInterest.Create(
            category,
            "Cham Museum",
            16.0471m,
            108.2068m,
            1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            averageVisitDurationMinutes: 60);
        poi.ConfigurePlanningMetadata(60_000m, "https://example.com/cham", DateTimeOffset.UtcNow);
        poi.AddOpeningHour(PoiOpeningHour.Create(2, new TimeOnly(7, 0), new TimeOnly(20, 0), false));
        dbContext.PointsOfInterest.Add(poi);
        await dbContext.SaveChangesAsync();
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

    private sealed class NoOpSchedulingRequestLock : ISchedulingRequestLock
    {
        public Task AcquireAsync(
            long travelerUserId,
            Guid idempotencyKey,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
