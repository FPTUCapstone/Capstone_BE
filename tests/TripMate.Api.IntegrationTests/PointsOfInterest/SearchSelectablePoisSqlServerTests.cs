using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Geo;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.PointsOfInterest.Search;
using TripMate.Application.Features.Scheduling.Common;
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
        await ApplySchedulingMigrationsAsync(database);
        var seed = await SeedBoundaryPoisAsync(database);
        using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString,
            configureTestServices: services =>
            {
                services.RemoveAll<IRouteDurationProvider>();
                services.AddScoped<IRouteDurationProvider, TestRouteDurationProvider>();
            });
        using var client = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);

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

        firstBody.RootElement.GetProperty("totalCount").GetInt32().Should().Be(6);
        secondBody.RootElement.GetProperty("totalCount").GetInt32().Should().Be(6);
        firstItem.GetProperty("name").GetString().Should().Be("Same Name");
        secondItem.GetProperty("name").GetString().Should().Be("Same Name");
        firstItem.GetProperty("id").GetInt64()
            .Should().BeLessThan(secondItem.GetProperty("id").GetInt64());

        using var allInsideResponse = await client.GetAsync(
            "/api/v1/points-of-interest/search?latitude=16&longitude=108&radiusKm=5"
            + "&page=1&pageSize=50");
        allInsideResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var allInsideBody = JsonDocument.Parse(await allInsideResponse.Content.ReadAsStringAsync());
        var returnedNames = allInsideBody.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(element => element.GetProperty("name").GetString())
            .ToHashSet();

        foreach (var insidePoi in seed.InsidePois)
        {
            returnedNames.Should().Contain(insidePoi.Name);

            var insideDistance = GeoDistance.EquirectangularKilometers(
                16m, 108m, insidePoi.Latitude, insidePoi.Longitude);
            insideDistance.Should().BeLessThan(5m);
            (insideDistance <= 5m).Should().BeTrue();

            using var acceptRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scheduling-requests")
            {
                Content = JsonContent.Create(new
                {
                    startAt = "2026-10-20T08:00:00+07:00",
                    timeZoneId = "Asia/Ho_Chi_Minh",
                    startLatitude = 16.0000m,
                    startLongitude = 108.0000m,
                    explorationLatitude = 16.0000m,
                    explorationLongitude = 108.0000m,
                    endPoiId = (long?)null,
                    returnToStart = true,
                    availableMinutes = 480,
                    transportMode = "Walking",
                    searchRadiusKm = 5m,
                    budgetVnd = 800_000m,
                    mandatoryPoiIds = new[] { insidePoi.Id },
                    restPreference = "None",
                }),
            };
            acceptRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var acceptResponse = await client.SendAsync(acceptRequest);
            acceptResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        foreach (var outsidePoi in seed.OutsidePois)
        {
            returnedNames.Should().NotContain(outsidePoi.Name);

            var outsideDistance = GeoDistance.EquirectangularKilometers(
                16m, 108m, outsidePoi.Latitude, outsidePoi.Longitude);
            outsideDistance.Should().BeGreaterThan(5m);
            (outsideDistance <= 5m).Should().BeFalse();

            using var rejectRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scheduling-requests")
            {
                Content = JsonContent.Create(new
                {
                    startAt = "2026-10-20T08:00:00+07:00",
                    timeZoneId = "Asia/Ho_Chi_Minh",
                    startLatitude = 16.0000m,
                    startLongitude = 108.0000m,
                    explorationLatitude = 16.0000m,
                    explorationLongitude = 108.0000m,
                    endPoiId = (long?)null,
                    returnToStart = true,
                    availableMinutes = 480,
                    transportMode = "Walking",
                    searchRadiusKm = 5m,
                    budgetVnd = 800_000m,
                    mandatoryPoiIds = new[] { outsidePoi.Id },
                    restPreference = "None",
                }),
            };
            rejectRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var rejectResponse = await client.SendAsync(rejectRequest);
            rejectResponse.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            var rejectBody = await rejectResponse.Content.ReadAsStringAsync();
            rejectBody.Should().Contain("outside the selected area");
        }
    }

    /// <summary>
    /// Verifies the tightest consecutive 6-decimal boundary pairs for the nearest representable equality strategy.
    /// Coordinate Quantization Note:
    /// In catalog.POIs, coordinates are stored as DECIMAL(9, 6) (microdegree resolution ~0.11m).
    /// Because LatitudeKilometersPerDegree = 110.574 = 55287 / 500 contains prime factor 6143 in its irreducible
    /// fraction, exact integer kilometer radii (e.g. 5 km -> delta = 2500 / 55287 deg) produce infinite repeating
    /// decimals in base 10 that cannot be represented as finite terminating 6-decimal numbers.
    /// The nearest representable 6-decimal numbers are adjacent microdegrees differing by exactly 0.000001 deg:
    /// - North: 16.045218 (d = 4.999935 km <= 5km) vs 16.045219 (d = 5.000046 km > 5km)
    /// - South: 15.954782 (d = 4.999935 km <= 5km) vs 15.954781 (d = 5.000046 km > 5km)
    /// - East:  108.046725 (d = 4.999933 km <= 5km) vs 108.046726 (d = 5.000040 km > 5km)
    /// - West:  107.953275 (d = 4.999933 km <= 5km) vs 107.953274 (d = 5.000040 km > 5km)
    /// </summary>
    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData("North", 16.045218, 108.000000, true)]
    [InlineData("North", 16.045219, 108.000000, false)]
    [InlineData("South", 15.954782, 108.000000, true)]
    [InlineData("South", 15.954781, 108.000000, false)]
    [InlineData("East", 16.000000, 108.046725, true)]
    [InlineData("East", 16.000000, 108.046726, false)]
    [InlineData("West", 16.000000, 107.953275, true)]
    [InlineData("West", 16.000000, 107.953274, false)]
    public void Boundary_Coordinates_MatchExpectedInsideOutside(
        string direction,
        double latitude,
        double longitude,
        bool expectedInside)
    {
        var distance = GeoDistance.EquirectangularKilometers(
            16m, 108m, (decimal)latitude, (decimal)longitude);
        if (expectedInside)
        {
            distance.Should().BeLessThan(5m, "direction {0} should be within radius", direction);
            (distance <= 5m).Should().BeTrue("direction {0} should be within radius", direction);
        }
        else
        {
            distance.Should().BeGreaterThan(5m, "direction {0} should be outside radius", direction);
            (distance <= 5m).Should().BeFalse("direction {0} should be outside radius", direction);
        }
    }

    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData(1.0)]
    [InlineData(5.0)]
    [InlineData(10.0)]
    [InlineData(50.0)]
    public void Boundary_ExactEquality_IsInclusive(double radiusDouble)
    {
        var radius = (decimal)radiusDouble;
        var exactDistance = radius;

        // When distance exactly equals the search radius, the boundary predicate
        // (distance <= radius) must evaluate to true, demonstrating inclusive boundary semantics.
        (exactDistance <= radius).Should().BeTrue("exact boundary equality must be inclusive at boundary");
        (exactDistance < radius).Should().BeFalse("exact equality is not strictly less than");
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

    private static async Task<BoundarySeed> SeedBoundaryPoisAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var traveler = new User
        {
            Email = "poi-boundary-sql@example.com",
            FullName = "POI Boundary SQL Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = SeedTime,
            UpdatedAtUtc = SeedTime,
        };
        var category = PoiCategory.Create("Museum", null);
        context.Users.Add(traveler);
        context.PoiCategories.Add(category);
        await context.SaveChangesAsync();

        var first = CreateSelectablePoi(category, traveler.Id, "Same Name", 16.001m, 108m);
        var second = CreateSelectablePoi(category, traveler.Id, "Same Name", 16.001m, 108m);

        // Nearest representable consecutive 6-decimal pairs (1e-6 deg resolution):
        // Each inside POI is < 0.07m inside 5km; each outside POI is < 0.05m outside 5km.
        var northInside = CreateSelectablePoi(category, traveler.Id, "North Inside", 16.045218m, 108.000000m);
        var northOutside = CreateSelectablePoi(category, traveler.Id, "North Outside", 16.045219m, 108.000000m);
        var southInside = CreateSelectablePoi(category, traveler.Id, "South Inside", 15.954782m, 108.000000m);
        var southOutside = CreateSelectablePoi(category, traveler.Id, "South Outside", 15.954781m, 108.000000m);
        var eastInside = CreateSelectablePoi(category, traveler.Id, "East Inside", 16.000000m, 108.046725m);
        var eastOutside = CreateSelectablePoi(category, traveler.Id, "East Outside", 16.000000m, 108.046726m);
        var westInside = CreateSelectablePoi(category, traveler.Id, "West Inside", 16.000000m, 107.953275m);
        var westOutside = CreateSelectablePoi(category, traveler.Id, "West Outside", 16.000000m, 107.953274m);

        var insidePois = new[] { northInside, southInside, eastInside, westInside };
        var outsidePois = new[] { northOutside, southOutside, eastOutside, westOutside };
        var tieBreakPois = new[] { first, second };

        context.PointsOfInterest.AddRange(first, second);
        context.PointsOfInterest.AddRange(insidePois);
        context.PointsOfInterest.AddRange(outsidePois);
        await context.SaveChangesAsync();

        return new BoundarySeed(traveler.Id, insidePois, outsidePois, tieBreakPois);
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
        for (byte day = 0; day <= 6; day++)
        {
            poi.AddOpeningHour(PoiOpeningHour.Create(
                day,
                new TimeOnly(7, 0),
                new TimeOnly(20, 0),
                false));
        }
        return poi;
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

    private sealed record BoundarySeed(
        long TravelerId,
        PointOfInterest[] InsidePois,
        PointOfInterest[] OutsidePois,
        PointOfInterest[] TieBreakPois);
}