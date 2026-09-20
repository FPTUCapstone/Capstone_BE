using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.PointsOfInterest.Search;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.PointsOfInterest.Search;

public class SearchSelectablePoisQueryHandlerTests
{
    [Fact]
    public async Task Handle_ReturnsOnlyActivePoisWithKnownOpeningHoursInsideRadius()
    {
        await using var dbContext = TestDbContext.Create();
        var category = PoiCategory.Create("Museum", null);
        dbContext.PoiCategories.Add(category);
        await dbContext.SaveChangesAsync();

        var eligible = CreatePoi(category, "Cham Museum", 16.043m, 108.222m);
        eligible.ConfigurePlanningMetadata(60_000m, "https://example.com/cham", SeedTime);
        eligible.AddOpeningHour(PoiOpeningHour.Create(1, new TimeOnly(7, 30), new TimeOnly(17, 0), false));
        var missingHours = CreatePoi(category, "Unverified POI", 16.044m, 108.223m);
        var farAway = CreatePoi(category, "Far Away", 16.300m, 108.300m);
        farAway.ConfigurePlanningMetadata(0m, "https://example.com/far-away", SeedTime);
        farAway.AddOpeningHour(PoiOpeningHour.Create(1, new TimeOnly(7, 30), new TimeOnly(17, 0), false));
        var missingVerification = CreatePoi(category, "Missing verification", 16.042m, 108.221m);
        missingVerification.AddOpeningHour(PoiOpeningHour.Create(1, new TimeOnly(7, 30), new TimeOnly(17, 0), false));
        dbContext.PointsOfInterest.AddRange(eligible, missingHours, farAway, missingVerification);
        await dbContext.SaveChangesAsync();

        var handler = new SearchSelectablePoisQueryHandler(dbContext);
        var result = await handler.Handle(
            new SearchSelectablePoisQuery(null, 16.043m, 108.222m, 5, 1, 20),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle();
        result.Value.Items.Single().Name.Should().Be("Cham Museum");
        result.Value.Items.Single().OpeningHoursKnown.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WithoutLocation_SearchesEligiblePoisByName()
    {
        await using var dbContext = TestDbContext.Create();
        var category = PoiCategory.Create("Museum", null);
        dbContext.PoiCategories.Add(category);
        await dbContext.SaveChangesAsync();

        var matchingPoi = CreatePoi(category, "Cham Museum", 16.043m, 108.222m);
        matchingPoi.ConfigurePlanningMetadata(60_000m, "https://example.com/cham", SeedTime);
        matchingPoi.AddOpeningHour(PoiOpeningHour.Create(1, new TimeOnly(7, 30), new TimeOnly(17, 0), false));
        var otherPoi = CreatePoi(category, "Museum of Da Nang", 16.044m, 108.223m);
        otherPoi.ConfigurePlanningMetadata(0m, "https://example.com/other", SeedTime);
        otherPoi.AddOpeningHour(PoiOpeningHour.Create(1, new TimeOnly(7, 30), new TimeOnly(17, 0), false));
        dbContext.PointsOfInterest.AddRange(matchingPoi, otherPoi);
        await dbContext.SaveChangesAsync();

        var handler = new SearchSelectablePoisQueryHandler(dbContext);
        var result = await handler.Handle(
            new SearchSelectablePoisQuery("Cham", null, null, null, 1, 20),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle();
        result.Value.Items.Single().Name.Should().Be("Cham Museum");
    }

    private static PointOfInterest CreatePoi(
        PoiCategory category,
        string name,
        decimal latitude,
        decimal longitude) =>
        PointOfInterest.Create(
            category,
            name,
            latitude,
            longitude,
            1,
            SeedTime);

    private static readonly DateTimeOffset SeedTime =
        new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);
}