using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Navigation.Common;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Routing;

namespace TripMate.Infrastructure.UnitTests.Routing;

public sealed class CatalogRouteDurationProviderTests
{
    [Fact]
    public async Task ExplicitPoiIdentity_PreservesPositiveMetricsBetweenColocatedDistinctPois()
    {
        await using var db = CreateContext();
        var (from, to) = await SeedAsync(db);
        db.Entry(to).Property(poi => poi.Latitude).CurrentValue = from.Latitude;
        db.Entry(to).Property(poi => poi.Longitude).CurrentValue = from.Longitude;
        db.RouteSegments.Add(RouteSegment.Create(to, from, 11m, 31, TransportMode.Car,
            null, 1, Guid.NewGuid(), DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var fallback = new Fallback();
        var provider = new CatalogRouteDurationProvider(db, fallback);
        RoutePoint[] points = [new(from.Latitude, from.Longitude, from.Id),
            new(to.Latitude, to.Longitude, to.Id)];

        var matrix = await provider.GetMatrixAsync(points, TransportMode.Car, default);

        matrix.GetMinutes(0, 1).Should().Be(23);
        matrix.GetMeters(0, 1).Should().Be(12500);
        matrix.GetMinutes(1, 0).Should().Be(31);
        fallback.Calls.Should().Be(0);
        to.TryDeactivate(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        var inactive = await provider.GetMatrixAsync(points, TransportMode.Car, default);
        inactive.GetMinutes(0, 1).Should().Be(8);
    }

    [Fact]
    public async Task RepeatedKnownPoiAndGpsAtItsLocation_AreZeroWithoutDiscardingAnotherPoisRoute()
    {
        await using var db = CreateContext();
        var (from, to) = await SeedAsync(db);
        db.RouteSegments.Add(RouteSegment.Create(to, from, 11m, 31, TransportMode.Car,
            null, 1, Guid.NewGuid(), DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var fallback = new Fallback();
        RoutePoint[] points = [new(from.Latitude, from.Longitude),
            new(from.Latitude, from.Longitude, from.Id), new(to.Latitude, to.Longitude, to.Id)];

        var matrix = await new CatalogRouteDurationProvider(db, fallback)
            .GetMatrixAsync(points, TransportMode.Car, default);

        matrix.GetMinutes(0, 1).Should().Be(0);
        matrix.GetMinutes(1, 0).Should().Be(0);
        matrix.GetMinutes(1, 2).Should().Be(23);
        fallback.Calls.Should().Be(0);
    }

    [Fact]
    public async Task MemoizedMatrices_KeepDifferentPoiIdentitiesAtTheSameCoordinatesSeparate()
    {
        var fallback = new Fallback();
        var provider = new MemoizingRouteDurationProvider(fallback);
        await provider.GetMatrixAsync([new(16m, 108m, 1), new(16.1m, 108.1m, 2)], TransportMode.Car, default);
        await provider.GetMatrixAsync([new(16m, 108m, 1), new(16.1m, 108.1m, 3)], TransportMode.Car, default);

        fallback.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Navigation_ExposesCurrentActiveRouteMetricsWithoutChangingItsHistoricalSchedule()
    {
        await using var db = CreateContext();
        var (from, to) = await SeedAsync(db);
        var now = DateTimeOffset.UtcNow;
        var response = new NavigationSessionResponse(1, 2, 3, "Navigating", null, now,
            now.AddHours(2), null, null, 2,
            [new(1, 1, from.Id, from.Name, from.Latitude, from.Longitude, now, now, true,
                "Pending", null, null, null),
             new(2, 2, to.Id, to.Name, to.Latitude, to.Longitude, now.AddHours(1), now.AddHours(1),
                true, "Pending", null, null, TransportMode.Car)], TransportMode.Car);

        var enriched = await ActiveRouteNavigationProjection.EnrichAsync(db, response, default);

        enriched.RouteLegs.Should().ContainSingle();
        enriched.RouteLegs.Single().DistanceKm.Should().Be(12.5m);
        enriched.RouteLegs.Single().EstimatedDurationMinutes.Should().Be(23);
        enriched.Items.Should().BeEquivalentTo(response.Items);
        to.TryDeactivate(now);
        await db.SaveChangesAsync();
        var inactive = await ActiveRouteNavigationProjection.EnrichAsync(db, response, default);
        inactive.RouteLegs.Should().BeEmpty();
    }

    [Fact]
    public async Task NewActiveRoute_OverridesOnlyItsDirectedModeSpecificPlanningEdge()
    {
        await using var db = CreateContext();
        var (from, to) = await SeedAsync(db);
        var fallback = new Fallback();
        var provider = new CatalogRouteDurationProvider(db, fallback);

        var matrix = await provider.GetMatrixAsync(Points(from, to), TransportMode.Car, default);

        matrix.GetMinutes(0, 1).Should().Be(23);
        matrix.GetMeters(0, 1).Should().Be(12500);
        matrix.GetMinutes(1, 0).Should().Be(8);
        fallback.Calls.Should().Be(1);
        var walking = await provider.GetMatrixAsync(Points(from, to), TransportMode.Walking, default);
        walking.GetMinutes(0, 1).Should().Be(8);
    }

    [Fact]
    public async Task DeactivatedEndpoint_IsNeverUsedForPlanning()
    {
        await using var db = CreateContext();
        var (from, to) = await SeedAsync(db);
        to.TryDeactivate(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        var provider = new CatalogRouteDurationProvider(db, new Fallback());

        var matrix = await provider.GetMatrixAsync(Points(from, to), TransportMode.Car, default);

        matrix.GetMinutes(0, 1).Should().Be(8);
    }

    [Fact]
    public async Task CompleteCatalogMatrix_DoesNotRequireMapServiceAvailability()
    {
        await using var db = CreateContext();
        var (from, to) = await SeedAsync(db);
        db.RouteSegments.Add(RouteSegment.Create(to, from, 11m, 31, TransportMode.Car, null,
            1, Guid.NewGuid(), DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var fallback = new Fallback();
        var provider = new CatalogRouteDurationProvider(db, fallback);

        var matrix = await provider.GetMatrixAsync(Points(from, to), TransportMode.Car, default);

        matrix.GetMinutes(0, 1).Should().Be(23);
        matrix.GetMinutes(1, 0).Should().Be(31);
        fallback.Calls.Should().Be(0);
    }

    [Fact]
    public async Task InactiveStoredRoute_IsNotUsedEvenWhenBothEndpointsAreActive()
    {
        await using var db = CreateContext();
        var (from, to) = await SeedAsync(db);
        var route = await db.RouteSegments.SingleAsync();
        db.Entry(route).Property(item => item.Status).CurrentValue = RouteStatus.Inactive;
        await db.SaveChangesAsync();
        var matrix = await new CatalogRouteDurationProvider(db, new Fallback())
            .GetMatrixAsync(Points(from, to), TransportMode.Car, default);
        matrix.GetMinutes(0, 1).Should().Be(8);
    }

    [Fact]
    public async Task ColocatedPoiWithoutItsOwnRoute_CannotInheritAnotherPoisStoredMetrics()
    {
        await using var db = CreateContext();
        var (from, to) = await SeedAsync(db);
        db.PointsOfInterest.Add(PointOfInterest.Create(from.Category, "Other origin", from.Latitude,
            from.Longitude, 1, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var matrix = await new CatalogRouteDurationProvider(db, new Fallback())
            .GetMatrixAsync(Points(from, to), TransportMode.Car, default);
        matrix.GetMinutes(0, 1).Should().Be(8);
    }

    [Fact]
    public async Task AmbiguousCoordinates_DoNotSelectAnArbitraryStoredRoute()
    {
        await using var db = CreateContext();
        var (from, to) = await SeedAsync(db);
        var duplicate = PointOfInterest.Create(from.Category, "Other origin", from.Latitude,
            from.Longitude, 1, DateTimeOffset.UtcNow);
        db.PointsOfInterest.Add(duplicate);
        await db.SaveChangesAsync();
        db.RouteSegments.Add(RouteSegment.Create(duplicate, to, 14m, 50, TransportMode.Car,
            null, 1, Guid.NewGuid(), DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var provider = new CatalogRouteDurationProvider(db, new Fallback());

        var matrix = await provider.GetMatrixAsync(Points(from, to), TransportMode.Car, default);

        matrix.GetMinutes(0, 1).Should().Be(8);
    }

    private static ApplicationDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static RoutePoint[] Points(PointOfInterest from, PointOfInterest to) =>
        [new(from.Latitude, from.Longitude), new(to.Latitude, to.Longitude)];

    private static async Task<(PointOfInterest, PointOfInterest)> SeedAsync(ApplicationDbContext db)
    {
        var category = PoiCategory.Create("Test places", null);
        var from = PointOfInterest.Create(category, "Origin", 16m, 108m, 1, DateTimeOffset.UtcNow);
        var to = PointOfInterest.Create(category, "Destination", 16.1m, 108.1m, 1, DateTimeOffset.UtcNow);
        db.PointsOfInterest.AddRange(from, to);
        await db.SaveChangesAsync();
        db.RouteSegments.Add(RouteSegment.Create(from, to, 12.5m, 23, TransportMode.Car,
            null, 1, Guid.NewGuid(), DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        return (from, to);
    }

    private sealed class Fallback : IRouteDurationProvider
    {
        public int Calls { get; private set; }
        public Task<RouteDurationMatrix> GetMatrixAsync(IReadOnlyList<RoutePoint> points,
            TransportMode transportMode, CancellationToken cancellationToken)
        {
            Calls++;
            var minutes = new int[points.Count, points.Count];
            var meters = new int[points.Count, points.Count];
            for (var i = 0; i < points.Count; i++)
            {
                for (var j = 0; j < points.Count; j++)
                {
                    minutes[i, j] = i == j ? 0 : 8;
                    meters[i, j] = i == j ? 0 : 800;
                }
            }
            return Task.FromResult(RouteDurationMatrix.Create(minutes, null, meters));
        }
    }
}