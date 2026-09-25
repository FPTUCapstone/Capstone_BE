using System.Net;
using System.Text.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.PointsOfInterest;

[Collection(nameof(TripMateApiFactory))]
public class SearchPointsOfInterestEndpointTests
{
    private static readonly DateTimeOffset SeedTime =
        new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Get_AsTraveler_ReturnsOnlySelectableContract()
    {
        await using var factory = new TripMateApiFactory();
        var travelerId = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(travelerId, UserRole.Traveler);

        var response = await client.GetAsync(
            "/api/v1/points-of-interest/search?latitude=16.043&longitude=108.222&radiusKm=5&page=1&pageSize=20");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = body.RootElement.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("name").GetString().Should().Be("Cham Museum");
        item.GetProperty("openingHoursKnown").GetBoolean().Should().BeTrue();
        item.TryGetProperty("description", out _).Should().BeFalse();
        item.TryGetProperty("createdByUserId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Get_WithoutAuthentication_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"),
        });

        var response = await client.GetAsync(
            "/api/v1/points-of-interest/search?latitude=16.043&longitude=108.222&radiusKm=5&page=1&pageSize=20");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_WithoutLocation_AllowsTravelerToSearchByName()
    {
        await using var factory = new TripMateApiFactory();
        var travelerId = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(travelerId, UserRole.Traveler);

        var response = await client.GetAsync(
            "/api/v1/points-of-interest/search?query=Cham&page=1&pageSize=20");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("items").EnumerateArray().Single()
            .GetProperty("name").GetString().Should().Be("Cham Museum");
    }

    private static Task<long> SeedAsync(TripMateApiFactory factory) =>
        factory.WithDbContextAsync(async context =>
        {
            var traveler = new User
            {
                Email = $"{Guid.NewGuid():N}@example.com",
                FullName = "Traveler",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = SeedTime,
                UpdatedAtUtc = SeedTime,
            };
            var category = PoiCategory.Create("Museum", null);
            context.Users.Add(traveler);
            context.PoiCategories.Add(category);
            await context.SaveChangesAsync();

            var poi = PointOfInterest.Create(
                category,
                "Cham Museum",
                16.043m,
                108.222m,
                traveler.Id,
                SeedTime);
            poi.ConfigurePlanningMetadata(
                60_000m,
                "https://example.com/cham-museum",
                SeedTime);
            poi.AddOpeningHour(PoiOpeningHour.Create(
                1,
                new TimeOnly(7, 30),
                new TimeOnly(17, 0),
                false));

            context.PointsOfInterest.Add(poi);
            await context.SaveChangesAsync();
            return traveler.Id;
        });
}