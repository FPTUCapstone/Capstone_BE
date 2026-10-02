using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Itineraries.Regenerate;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Itineraries;

public sealed class ItineraryVersionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Regenerate_UsesSchedulingRequestAsTheMutationLockResource()
    {
        await using var dbContext = TestDbContext.Create();
        var request = await AddRequestAfterTwoUnrelatedRequestsAsync(dbContext);
        var itinerary = await AddItineraryAsync(dbContext, request);
        var mutationLock = new RecordingItineraryMutationLock();
        var handler = new RegenerateItineraryCommandHandler(
            dbContext,
            new ItineraryAccessService(dbContext),
            new ItineraryVersionService(dbContext, new FixedRouteDurationProvider()),
            mutationLock,
            new FakeDateTimeProvider { UtcNow = Now });

        _ = await handler.Handle(
            new RegenerateItineraryCommand(itinerary.Id, request.TravelerUserId, Guid.NewGuid()),
            CancellationToken.None);

        mutationLock.AcquiredResources.Should().Equal(request.Id);
        request.Id.Should().NotBe(itinerary.Id);
    }

    [Fact]
    public async Task CreateRegeneratedVersion_UsesSavedTravelerInterestTagsToRankOptionalPois()
    {
        await using var dbContext = TestDbContext.Create();
        const long travelerUserId = 42;
        var request = await AddRequestAsync(dbContext, travelerUserId);
        var original = await AddItineraryAsync(dbContext, request);
        var preferred = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Preferred museum");
        _ = await AddPlanningReadyPoiAsync(dbContext, "Nature", "Other attraction");
        dbContext.TravelerProfiles.Add(TravelerProfile.Create(
            travelerUserId,
            "[\"culture\"]",
            Now));
        await dbContext.SaveChangesAsync();

        var result = await new ItineraryVersionService(
                dbContext,
                new FixedRouteDurationProvider())
            .CreateRegeneratedVersionAsync(original, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items
            .Where(item => item.PointOfInterestId.HasValue)
            .Select(item => item.PointOfInterestId)
            .Should().StartWith(preferred.Id);
    }

    [Fact]
    public async Task CreateRegeneratedVersion_PersistsRoadDurationForTheNextItineraryItem()
    {
        await using var dbContext = TestDbContext.Create();
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var original = await AddItineraryAsync(dbContext, request);
        _ = await AddPlanningReadyPoiAsync(dbContext, "Culture", "First stop");
        _ = await AddPlanningReadyPoiAsync(dbContext, "Nature", "Second stop");

        var result = await new ItineraryVersionService(
                dbContext,
                new FixedRouteDurationProvider())
            .CreateRegeneratedVersionAsync(original, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items
            .OrderBy(item => item.SequenceNo)
            .First()
            .TravelDurationToNextMinutes
            .Should().Be(10);
    }

    private static async Task<SchedulingRequest> AddRequestAfterTwoUnrelatedRequestsAsync(TestDbContext dbContext)
    {
        dbContext.SchedulingRequests.AddRange(
            CreateRequest(40, Guid.NewGuid()),
            CreateRequest(41, Guid.NewGuid()));
        await dbContext.SaveChangesAsync();
        return await AddRequestAsync(dbContext, 42);
    }

    private static async Task<SchedulingRequest> AddRequestAsync(TestDbContext dbContext, long travelerUserId)
    {
        var request = CreateRequest(travelerUserId, Guid.NewGuid());
        request.Complete(Now);
        dbContext.SchedulingRequests.Add(request);
        await dbContext.SaveChangesAsync();
        return request;
    }

    private static SchedulingRequest CreateRequest(long travelerUserId, Guid idempotencyKey) =>
        SchedulingRequest.Create(
            travelerUserId,
            idempotencyKey,
            new string('a', SchedulingRequest.RequestHashLength),
            Now.AddHours(1),
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
            Now);

    private static async Task<Itinerary> AddItineraryAsync(
        TestDbContext dbContext,
        SchedulingRequest request)
    {
        var itinerary = Itinerary.CreateCspGenerated(
            request,
            "Generated trip",
            Now.AddHours(1),
            Now.AddHours(5));
        dbContext.Itineraries.Add(itinerary);
        await dbContext.SaveChangesAsync();
        return itinerary;
    }

    private static async Task<PointOfInterest> AddPlanningReadyPoiAsync(
        TestDbContext dbContext,
        string categoryName,
        string poiName)
    {
        var poi = PointOfInterest.Create(
            PoiCategory.Create(categoryName, null),
            poiName,
            16.0471m,
            108.2068m,
            1,
            Now,
            averageVisitDurationMinutes: 60);
        poi.ConfigurePlanningMetadata(60_000m, "https://example.com/poi", Now);
        poi.AddOpeningHour(PoiOpeningHour.Create(
            2,
            new TimeOnly(7, 0),
            new TimeOnly(20, 0),
            false));
        dbContext.PointsOfInterest.Add(poi);
        await dbContext.SaveChangesAsync();
        return poi;
    }

    private sealed class RecordingItineraryMutationLock : IItineraryMutationLock
    {
        public List<long> AcquiredResources { get; } = [];

        public Task AcquireAsync(long itineraryId, CancellationToken cancellationToken)
        {
            AcquiredResources.Add(itineraryId);
            return Task.CompletedTask;
        }
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