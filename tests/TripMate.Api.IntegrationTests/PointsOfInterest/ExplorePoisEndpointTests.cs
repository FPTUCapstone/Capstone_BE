using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.PointsOfInterest.Explore;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.PointsOfInterest;

[Collection(nameof(TripMateApiFactory))]
public class ExplorePoisEndpointTests
{
    private static readonly DateTimeOffset SeedTime =
        new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Get_Anonymous_ReturnsOkWithPagedPoiResponseDto()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/pois");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PagedPoiResponseDto>();
        body.Should().NotBeNull();
        body!.Page.Should().Be(1);
        body.PageSize.Should().Be(20);
        body.TotalCount.Should().Be(1);
        body.TotalPages.Should().Be(1);
        body.Items.Should().ContainSingle(item => item.Id == seed.PoiId && item.Name == "Da Lat Flower Park");
    }

    [Fact]
    public async Task Get_AuthenticatedTraveler_ReturnsSameOkResult()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.UserId, UserRole.Traveler);

        var response = await client.GetAsync("/api/v1/pois");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PagedPoiResponseDto>();
        body.Should().NotBeNull();
        body!.TotalCount.Should().Be(1);
        body.Items.Should().ContainSingle(item => item.Id == seed.PoiId);
    }

    [Fact]
    public async Task Get_WithInvalidQueryParams_Returns400ValidationProblemDetailsWithCamelCaseKeys()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/api/v1/pois?page=0&pageSize=101&sort=invalid&originLatitude=10&maxDistanceKm=5");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = problem.RootElement.GetProperty("errors");

        errors.TryGetProperty("page", out _).Should().BeTrue();
        errors.TryGetProperty("pageSize", out _).Should().BeTrue();
        errors.TryGetProperty("sort", out _).Should().BeTrue();
        errors.TryGetProperty("originLongitude", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Get_WithNoMatchingPois_Returns200WithEmptyPagedResult()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/pois?search=NonExistentPlace999");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PagedPoiResponseDto>();
        body.Should().NotBeNull();
        body!.TotalCount.Should().Be(0);
        body.TotalPages.Should().Be(0);
        body.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_WhenPersistenceFails_ReturnsSanitized500ProblemDetails()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<IApplicationDbContext>(_ => throw new InvalidOperationException("Simulated database failure"));
            });
        }).CreateClient();

        var response = await client.GetAsync("/api/v1/pois");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var responseBody = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(responseBody);
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(500);
        problem.RootElement.GetProperty("title").GetString().Should().Be("An unexpected error occurred.");
        responseBody.Should().NotContain("Simulated database failure");
        responseBody.Should().NotContain("InvalidOperationException");
    }

    private static Task<SeedResult> SeedAsync(TripMateApiFactory factory) =>
        factory.WithDbContextAsync(async context =>
        {
            var user = new User
            {
                Email = $"{Guid.NewGuid():N}@example.com",
                FullName = "API Test User",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = SeedTime,
                UpdatedAtUtc = SeedTime,
            };
            var category = PoiCategory.Create("Attraction", null);
            context.Users.Add(user);
            context.PoiCategories.Add(category);
            await context.SaveChangesAsync();

            var poi = PointOfInterest.Create(
                category,
                "Da Lat Flower Park",
                11.941755m,
                108.438278m,
                user.Id,
                SeedTime);
            context.PointsOfInterest.Add(poi);
            await context.SaveChangesAsync();

            return new SeedResult(user.Id, category.Id, poi.Id);
        });

    private sealed record SeedResult(long UserId, int CategoryId, long PoiId);
}