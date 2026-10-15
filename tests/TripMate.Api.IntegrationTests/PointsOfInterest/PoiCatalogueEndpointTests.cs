using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.PointsOfInterest;

[Collection(nameof(TripMateApiFactory))]
public class PoiCatalogueEndpointTests
{
    private const string CatalogueRoute = "/api/v1/admin/pois/catalogue";

    private static readonly DateTimeOffset SeedTime =
        new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Get_WithStaff_ReturnsCategoriesAndTagsSortedByName()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory, UserRole.Staff, includeCatalogue: true);
        using var client = factory.CreateAuthenticatedClient(seed.UserId, UserRole.Staff);

        var response = await client.GetAsync(CatalogueRoute);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var categories = body.RootElement.GetProperty("categories").EnumerateArray().ToList();
        categories.Select(item => item.GetProperty("name").GetString())
            .Should().Equal("Attraction", "Restaurant");
        categories.Should().OnlyContain(item =>
            item.GetProperty("id").GetInt32() > 0
            && item.EnumerateObject().Count() == 2);
        body.RootElement.GetProperty("tags").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .Should().Equal("Beach", "Family");
    }

    [Fact]
    public async Task Get_WithStaffAndNoCatalogueData_ReturnsEmptyArrays()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory, UserRole.Staff, includeCatalogue: false);
        using var client = factory.CreateAuthenticatedClient(seed.UserId, UserRole.Staff);

        var response = await client.GetAsync(CatalogueRoute);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("categories").GetArrayLength().Should().Be(0);
        body.RootElement.GetProperty("tags").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Get_WithoutAuthentication_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"),
        });

        var response = await client.GetAsync(CatalogueRoute);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.TourOperator)]
    [InlineData(UserRole.Administrator)]
    public async Task Get_WithNonStaffRole_ReturnsForbiddenProblemDetails(UserRole role)
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory, role, includeCatalogue: true);
        using var client = factory.CreateAuthenticatedClient(seed.UserId, role);

        var response = await client.GetAsync(CatalogueRoute);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(PoiErrorCodes.AdminAccessRequired);
    }

    private static Task<SeedResult> SeedAsync(
        TripMateApiFactory factory,
        UserRole role,
        bool includeCatalogue) =>
        factory.WithDbContextAsync(async context =>
        {
            var user = new User
            {
                Email = $"{Guid.NewGuid():N}@example.com",
                FullName = "API Test User",
                Role = role,
                Status = AccountStatus.Active,
                CreatedAtUtc = SeedTime,
                UpdatedAtUtc = SeedTime,
            };
            context.Users.Add(user);

            if (includeCatalogue)
            {
                context.PoiCategories.AddRange(
                    PoiCategory.Create("Restaurant", null),
                    PoiCategory.Create("Attraction", "Sights"));
                context.Tags.AddRange(Tag.Create("Family"), Tag.Create("Beach"));
            }

            await context.SaveChangesAsync();
            return new SeedResult(user.Id);
        });

    private sealed record SeedResult(long UserId);
}