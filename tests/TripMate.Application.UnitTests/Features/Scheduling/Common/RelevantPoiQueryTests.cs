using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Scheduling.Common;

public sealed class RelevantPoiQueryTests
{
    [Fact]
    public async Task WithPlanningDetails_ExplorationAroundHoaPhuThanh_ExcludesDowntownPoisOutsideRadius()
    {
        await using var dbContext = TestDbContext.Create();

        // Hoa Phu Thanh Tourist Area (Suburban / Mountainous area)
        var hoaPhuThanh = await SeedPoiAsync(
            dbContext,
            "Hoa Phu Thanh Tourist Area",
            15.973945m,
            108.090233m);

        // Than Tai Hot Spring Park (~11 km from Hoa Phu Thanh, within 12km search radius)
        var thanTai = await SeedPoiAsync(
            dbContext,
            "Than Tai Hot Spring Park",
            15.987264m,
            107.986255m);

        // Marble Mountains (~18.8 km from Hoa Phu Thanh in downtown / coastal Da Nang, OUTSIDE 12km radius)
        var marbleMountains = await SeedPoiAsync(
            dbContext,
            "Marble Mountains - Thuy Son Peak",
            16.004052m,
            108.263595m);

        // Military Zone 5 Museum (~16.2 km from Hoa Phu Thanh, OUTSIDE 12km radius)
        var militaryMuseum = await SeedPoiAsync(
            dbContext,
            "Military Zone 5 Museum",
            16.054000m,
            108.216000m);

        decimal explorationLat = 15.973945m;
        decimal explorationLng = 108.090233m;
        decimal searchRadiusKm = 12m;

        var results = await dbContext.PointsOfInterest
            .WithPlanningDetails(
                explorationLat,
                explorationLng,
                searchRadiusKm,
                mandatoryPoiIds: Array.Empty<long>(),
                endPoiId: null)
            .ToListAsync();

        var returnedIds = results.Select(p => p.Id).ToList();

        returnedIds.Should().Contain(hoaPhuThanh.Id);
        returnedIds.Should().Contain(thanTai.Id);
        returnedIds.Should().NotContain(marbleMountains.Id);
        returnedIds.Should().NotContain(militaryMuseum.Id);
    }

    [Fact]
    public async Task WithPlanningDetails_WhenDowntownPoiIsMandatory_IncludesItDespiteBeingOutsideRadius()
    {
        await using var dbContext = TestDbContext.Create();

        var hoaPhuThanh = await SeedPoiAsync(
            dbContext,
            "Hoa Phu Thanh Tourist Area",
            15.973945m,
            108.090233m);

        var marbleMountains = await SeedPoiAsync(
            dbContext,
            "Marble Mountains - Thuy Son Peak",
            16.004052m,
            108.263595m);

        decimal explorationLat = 15.973945m;
        decimal explorationLng = 108.090233m;
        decimal searchRadiusKm = 12m;

        var results = await dbContext.PointsOfInterest
            .WithPlanningDetails(
                explorationLat,
                explorationLng,
                searchRadiusKm,
                mandatoryPoiIds: [marbleMountains.Id],
                endPoiId: null)
            .ToListAsync();

        var returnedIds = results.Select(p => p.Id).ToList();

        returnedIds.Should().Contain(hoaPhuThanh.Id);
        returnedIds.Should().Contain(marbleMountains.Id);
    }

    [Fact]
    public async Task WithPlanningDetails_WhenEndPoiIsOutsideRadius_IncludesIt()
    {
        await using var dbContext = TestDbContext.Create();

        var hoaPhuThanh = await SeedPoiAsync(
            dbContext,
            "Hoa Phu Thanh Tourist Area",
            15.973945m,
            108.090233m);

        var airportHotel = await SeedPoiAsync(
            dbContext,
            "Da Nang Airport Hotel",
            16.050000m,
            108.200000m);

        decimal explorationLat = 15.973945m;
        decimal explorationLng = 108.090233m;
        decimal searchRadiusKm = 12m;

        var results = await dbContext.PointsOfInterest
            .WithPlanningDetails(
                explorationLat,
                explorationLng,
                searchRadiusKm,
                mandatoryPoiIds: Array.Empty<long>(),
                endPoiId: airportHotel.Id)
            .ToListAsync();

        var returnedIds = results.Select(p => p.Id).ToList();

        returnedIds.Should().Contain(hoaPhuThanh.Id);
        returnedIds.Should().Contain(airportHotel.Id);
    }

    private static async Task<PointOfInterest> SeedPoiAsync(
        TestDbContext dbContext,
        string name,
        decimal latitude,
        decimal longitude)
    {
        var category = PoiCategory.Create("Attraction", null);
        var poi = PointOfInterest.Create(
            category,
            name,
            latitude,
            longitude,
            1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            averageVisitDurationMinutes: 60);

        poi.ConfigurePlanningMetadata(
            50_000m,
            $"https://example.com/{name.ToLowerInvariant().Replace(' ', '-')}",
            DateTimeOffset.UtcNow);

        poi.AddOpeningHour(PoiOpeningHour.Create(
            (byte)DayOfWeek.Monday,
            new TimeOnly(8, 0),
            new TimeOnly(18, 0),
            false));

        await dbContext.PointsOfInterest.AddAsync(poi);
        await dbContext.SaveChangesAsync();
        return poi;
    }
}