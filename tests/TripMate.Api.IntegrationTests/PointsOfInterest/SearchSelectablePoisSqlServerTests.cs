using System.Net;
using System.Text.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Geo;
using TripMate.Application.Features.PointsOfInterest.Search;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.PointsOfInterest;

public sealed class SearchSelectablePoisSqlServerTests
{
    private static readonly DateTimeOffset SeedTime =
        new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Search_WithLocation_ExecutesRadiusCountAndStablePagingInSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedSelectablePoisAsync(database);

        await using var context = database.CreateDbContext();
        var handler = new SearchSelectablePoisQueryHandler(context);
        var firstPage = await handler.Handle(
            new SearchSelectablePoisQuery(null, 16.0000m, 108.0000m, 5, 1, 1),
            CancellationToken.None);
        var secondPage = await handler.Handle(
            new SearchSelectablePoisQuery(null, 16.0000m, 108.0000m, 5, 2, 1),
            CancellationToken.None);
        var outOfRangePage = await handler.Handle(
            new SearchSelectablePoisQuery(null, 16.0000m, 108.0000m, 5, 3, 1),
            CancellationToken.None);
        var extremeOffsetPage = await handler.Handle(
            new SearchSelectablePoisQuery(null, 16.0000m, 108.0000m, 5, int.MaxValue, 50),
            CancellationToken.None);

        firstPage.IsSuccess.Should().BeTrue();
        firstPage.Value.TotalCount.Should().Be(2);
        firstPage.Value.Items.Select(item => item.Name).Should().Equal("Near Alpha");
        secondPage.IsSuccess.Should().BeTrue();
        secondPage.Value.TotalCount.Should().Be(2);
        secondPage.Value.Items.Select(item => item.Name).Should().Equal("Near Bravo");
        outOfRangePage.Value.TotalCount.Should().Be(2);
        outOfRangePage.Value.Items.Should().BeEmpty();
        extremeOffsetPage.Value.TotalCount.Should().Be(2);
        extremeOffsetPage.Value.Items.Should().BeEmpty();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Search_Endpoint_UsesSharedRadiusBoundaryAndStableIdTieBreakInSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedBoundaryPoisAsync(database);
        using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(100, UserRole.Traveler);

        using var firstResponse = await client.GetAsync(
            "/api/v1/points-of-interest/search?latitude=16&longitude=108&radiusKm=5"
            + "&page=1&pageSize=1");
        using var secondResponse = await client.GetAsync(
            "/api/v1/points-of-interest/search?latitude=16&longitude=108&radiusKm=5"
            + "&page=2&pageSize=1");

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var firstBody = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        using var secondBody = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
        var firstItem = firstBody.RootElement.GetProperty("items").EnumerateArray().Single();
        var secondItem = secondBody.RootElement.GetProperty("items").EnumerateArray().Single();

        firstBody.RootElement.GetProperty("totalCount").GetInt32().Should().Be(3);
        secondBody.RootElement.GetProperty("totalCount").GetInt32().Should().Be(3);
        firstItem.GetProperty("name").GetString().Should().Be("Same Name");
        secondItem.GetProperty("name").GetString().Should().Be("Same Name");
        firstItem.GetProperty("id").GetInt64()
            .Should().BeLessThan(secondItem.GetProperty("id").GetInt64());

        GeoDistance.EquirectangularKilometers(16m, 108m, 16.045m, 108m)
            .Should().BeLessThan(5m);
        GeoDistance.EquirectangularKilometers(16m, 108m, 16.046m, 108m)
            .Should().BeGreaterThan(5m);
    }

    private static async Task SeedSelectablePoisAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var traveler = new User
        {
            Email = "poi-search-sql@example.com",
            FullName = "POI Search SQL Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = SeedTime,
            UpdatedAtUtc = SeedTime,
        };
        var category = PoiCategory.Create("Museum", null);
        context.Users.Add(traveler);
        context.PoiCategories.Add(category);
        await context.SaveChangesAsync();

        context.PointsOfInterest.AddRange(
            CreateSelectablePoi(category, traveler.Id, "Near Alpha", 16.0005m, 108.0000m),
            CreateSelectablePoi(category, traveler.Id, "Near Bravo", 16.0010m, 108.0000m),
            CreateSelectablePoi(category, traveler.Id, "Outside Radius", 16.1000m, 108.0000m));
        await context.SaveChangesAsync();
    }

    private static async Task SeedBoundaryPoisAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var category = PoiCategory.Create("Museum", null);
        context.PoiCategories.Add(category);
        await context.SaveChangesAsync();

        var first = CreateSelectablePoi(category, 100, "Same Name", 16.001m, 108m);
        var second = CreateSelectablePoi(category, 100, "Same Name", 16.001m, 108m);
        var boundaryInside = CreateSelectablePoi(category, 100, "Boundary Inside", 16.045m, 108m);
        var boundaryOutside = CreateSelectablePoi(category, 100, "Boundary Outside", 16.046m, 108m);
        context.PointsOfInterest.AddRange(first, second, boundaryInside, boundaryOutside);
        await context.SaveChangesAsync();
    }

    private static PointOfInterest CreateSelectablePoi(
        PoiCategory category,
        long travelerId,
        string name,
        decimal latitude,
        decimal longitude)
    {
        var poi = PointOfInterest.Create(
            category,
            name,
            latitude,
            longitude,
            travelerId,
            SeedTime);
        poi.ConfigurePlanningMetadata(0m, "https://example.com/poi-search-sql", SeedTime);
        poi.AddOpeningHour(PoiOpeningHour.Create(
            1,
            new TimeOnly(7, 0),
            new TimeOnly(20, 0),
            false));
        return poi;
    }
}
