using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Navigation.Common;
using TripMate.Application.Features.Routes;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Create;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Routing;

namespace TripMate.Api.IntegrationTests.Routes;

[Collection(nameof(TripMateApiFactory))]
public sealed class RouteConsumersSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task NewlyCreatedRoutes_AreConsumedBySchedulingHandler_PersistLegMetrics_AndAreAvailableToNavigation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await CreateRouteSqlServerTests.SeedAsync(database);
        var clock = new TestClock();
        await using (var setup = database.CreateDbContext())
        {
            var pois = await setup.PointsOfInterest.OrderBy(poi => poi.Id).ToListAsync();
            foreach (var poi in pois)
            {
                poi.ConfigurePlanningMetadata(0m, "https://example.com/tm101-sql", clock.UtcNow);
                for (byte day = 0; day < 7; day++)
                {
                    poi.AddOpeningHour(PoiOpeningHour.Create(day, new TimeOnly(7, 0), new TimeOnly(20, 0), false));
                }
            }
            await setup.SaveChangesAsync();
        }

        long forwardRouteId;
        await using (var create = database.CreateDbContext())
        {
            var routeHandler = new CreateRouteCommandHandler(create, new Staff(seed.Staff.Id), clock);
            var result = await routeHandler.Handle(Command(seed.FromId, seed.ToId, 12.5m, 23, "PublicTransit"), default);
            result.IsSuccess.Should().BeTrue();
            forwardRouteId = result.Value.Id;
            (await routeHandler.Handle(Command(seed.ToId, seed.FromId, 11m, 31, "PublicTransit"), default))
                .IsSuccess.Should().BeTrue();
        }

        long travelerId;
        await using (var users = database.CreateDbContext())
        {
            var traveler = new User
            {
                Email = "tm101-consumer@example.com",
                FullName = "Route consumer",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = clock.UtcNow,
                UpdatedAtUtc = clock.UtcNow,
            };
            users.Users.Add(traveler);
            await users.SaveChangesAsync();
            travelerId = traveler.Id;
        }

        long itineraryId;
        await using (var planning = database.CreateDbContext())
        {
            var fallback = new RejectFallbackProvider();
            var routeProvider = new CatalogRouteDurationProvider(planning, fallback);
            var handler = new CreateSchedulingRequestCommandHandler(planning, clock, routeProvider,
                new SqlServerSchedulingRequestLock(planning),
                new PoiRankingOrchestrator(new PersonalBehaviorFeatureAggregator(planning),
                    ProviderDisabledPoiRankingProvider.Instance, new PersonalizationRankingOptions(),
                    providerEnabled: false, NullLogger<PoiRankingOrchestrator>.Instance));
            var command = new CreateSchedulingRequestCommand(travelerId, Guid.NewGuid(),
                new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.FromHours(7)),
                "Asia/Ho_Chi_Minh", 16.06m, 108.20m, 16.10m, 108.25m, seed.ToId,
                false, 480, TransportMode.PublicTransit, 10m, null,
                [seed.FromId, seed.ToId], RestPreference.None);
            var result = await handler.Handle(command, default);
            result.IsSuccess.Should().BeTrue($"PC-02 scheduling must use the newly committed route: {result.ErrorCode} {result.ErrorMessage}");
            itineraryId = result.Value.ItineraryId;
            var origin = result.Value.Items.Single(item => item.PoiId == seed.FromId);
            origin.TravelDurationToNextMinutes.Should().Be(23);
            origin.TravelDistanceToNextMeters.Should().Be(12500);
            origin.TransportModeToNext.Should().Be(TransportMode.PublicTransit);
            fallback.Calls.Should().Be(0, "the complete stored transit matrix requires no unsupported map fallback");
        }

        await using var verify = database.CreateDbContext();
        var persisted = await verify.ItineraryItems.AsNoTracking()
            .SingleAsync(item => item.ItineraryId == itineraryId && item.PointOfInterestId == seed.FromId);
        persisted.TravelDurationToNextMinutes.Should().Be(23);
        persisted.TravelDistanceToNextMeters.Should().Be(12500);
        persisted.TransportModeToNext.Should().Be(TransportMode.PublicTransit);
        var navigation = await ActiveRouteNavigationProjection.EnrichAsync(verify, Navigation(seed), default);
        navigation.RouteLegs.Should().ContainSingle(leg => leg.Id == forwardRouteId
            && leg.DistanceKm == 12.5m && leg.EstimatedDurationMinutes == 23
            && leg.TransportMode == TransportMode.PublicTransit);

        // Only the planner representation is bounded. Navigation and historical timing retain their data.
        await database.ExecuteNonQueryAsync($"UPDATE catalog.RouteSegments SET est_duration_minutes={int.MaxValue} WHERE segment_id={forwardRouteId};");
        var catalog = await verify.RouteSegments.AsNoTracking().SingleAsync(route => route.Id == forwardRouteId);
        catalog.EstimatedDurationMinutes.Should().Be(int.MaxValue);
        var extremeNavigation = await ActiveRouteNavigationProjection.EnrichAsync(verify, Navigation(seed), default);
        extremeNavigation.RouteLegs.Should().ContainSingle(leg => leg.Id == forwardRouteId
            && leg.EstimatedDurationMinutes == int.MaxValue);
        var matrix = await new CatalogRouteDurationProvider(verify, new RejectFallbackProvider()).GetMatrixAsync(
            [new RoutePoint(16.06m, 108.20m, seed.FromId), new RoutePoint(16.10m, 108.25m, seed.ToId)],
            TransportMode.PublicTransit, default);
        matrix.GetMinutes(0, 1).Should().Be(SchedulingRequest.MaxAvailableMinutes + 1);
        matrix.GetMinutes(1, 0).Should().Be(31);
        (await verify.ItineraryItems.AsNoTracking().SingleAsync(item => item.Id == persisted.Id))
            .TravelDurationToNextMinutes.Should().Be(23);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task InactiveRouteWithActiveEndpoints_IsExcludedFromPlanningAndNavigation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await CreateRouteSqlServerTests.SeedAsync(database);
        await using (var create = database.CreateDbContext())
        {
            var result = await new CreateRouteCommandHandler(create, new Staff(seed.Staff.Id), new TestClock())
                .Handle(Command(seed.FromId, seed.ToId, 12.5m, 23, "Car"), default);
            result.IsSuccess.Should().BeTrue();
        }
        await database.ExecuteNonQueryAsync("UPDATE catalog.RouteSegments SET status='Inactive';");
        await using var context = database.CreateDbContext();
        (await context.PointsOfInterest.AllAsync(poi => poi.Status == PointOfInterestStatus.Active)).Should().BeTrue();
        var fallback = new FixedFallbackProvider();
        var matrix = await new CatalogRouteDurationProvider(context, fallback).GetMatrixAsync(
            [new RoutePoint(16.06m, 108.20m), new RoutePoint(16.10m, 108.25m)], TransportMode.Car, default);
        matrix.GetMinutes(0, 1).Should().Be(99, "inactive catalog metrics must not override the map provider");
        var response = Navigation(seed) with { TransportMode = TransportMode.Car };
        (await ActiveRouteNavigationProjection.EnrichAsync(context, response, default)).RouteLegs.Should().BeEmpty();
    }

    private static CreateRouteCommand Command(long from, long to, decimal distance, int duration, string mode) => new()
    {
        FromPoiId = from,
        ToPoiId = to,
        DistanceKm = distance,
        EstimatedDurationMinutes = duration,
        TransportMode = mode,
        IdempotencyKey = Guid.NewGuid(),
    };

    private static NavigationSessionResponse Navigation(CreateRouteSqlServerTests.Seed seed) => new(
        1, 1, 1, TripSession.NavigatingState, null, new TestClock().UtcNow, null, null, null, null,
        [Item(1, seed.FromId, "Origin", 16.06m, 108.20m), Item(2, seed.ToId, "Destination", 16.10m, 108.25m)],
        TransportMode.PublicTransit);

    private static NavigationSessionItemResponse Item(int sequence, long poiId, string name, decimal lat, decimal lng) =>
        new(sequence, sequence, poiId, name, lat, lng, new TestClock().UtcNow, new TestClock().UtcNow.AddHours(1),
            true, TripSessionItem.PendingStatus, null, null, null);

    private sealed class TestClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class Staff(long userId) : ICurrentUserService
    {
        public long? UserId => userId;
        public string? Role => nameof(UserRole.Staff);
    }

    private sealed class RejectFallbackProvider : IRouteDurationProvider
    {
        public int Calls { get; private set; }
        public Task<RouteDurationMatrix> GetMatrixAsync(IReadOnlyList<RoutePoint> points, TransportMode mode, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("Complete stored transit matrix must not invoke fallback.");
        }
    }

    private sealed class FixedFallbackProvider : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(IReadOnlyList<RoutePoint> points, TransportMode mode, CancellationToken ct) =>
            Task.FromResult(RouteDurationMatrix.Create(new[,] { { 0, 99 }, { 99, 0 } }, unreachable: null, meters: new[,] { { 0, 99000 }, { 99000, 0 } }));
    }
}