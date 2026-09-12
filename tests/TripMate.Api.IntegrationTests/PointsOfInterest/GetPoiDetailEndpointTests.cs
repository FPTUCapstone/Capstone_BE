using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Application.Features.PointsOfInterest.Detail;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.PointsOfInterest;

[Collection(nameof(TripMateApiFactory))]
public class GetPoiDetailEndpointTests
{
    private static readonly DateTimeOffset SeedTime =
        new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Get_Anonymous_WithActivePoi_Returns200WithDetailDtoWithoutCreatorData()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/pois/{seed.PoiId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PoiDetailDto>();
        body.Should().NotBeNull();
        body!.Id.Should().Be(seed.PoiId);
        body.Name.Should().Be("Da Lat Flower Park");
        body.Status.Should().Be(PointOfInterestStatus.Active);
        body.CategoryName.Should().Be("Attraction");

        using var jsonDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        jsonDoc.RootElement.TryGetProperty("createdById", out _).Should().BeFalse();
        jsonDoc.RootElement.TryGetProperty("createdBy", out _).Should().BeFalse();
        jsonDoc.RootElement.TryGetProperty("user", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Get_AuthenticatedTraveler_ReturnsSameDetailResult()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.UserId, UserRole.Traveler);

        var response = await client.GetAsync($"/api/v1/pois/{seed.PoiId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PoiDetailDto>();
        body.Should().NotBeNull();
        body!.Id.Should().Be(seed.PoiId);
    }

    [Fact]
    public async Task Get_WithNonExistentId_Returns404ProblemDetailsWithPoiNotFound()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/pois/999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(404);
        problem.RootElement.GetProperty("title").GetString().Should().Be(PoiErrorMessages.NotFound);
        problem.RootElement.GetProperty("errorCode").GetString().Should().Be(PoiErrorCodes.NotFound);
    }

    [Fact]
    public async Task Get_WithInactivePoi_Returns404ProblemDetailsWithPoiNotFound()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory, poiStatus: PointOfInterestStatus.Inactive);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/pois/{seed.PoiId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(404);
        problem.RootElement.GetProperty("errorCode").GetString().Should().Be(PoiErrorCodes.NotFound);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Get_WithInvalidId_Returns400ValidationProblemDetails(long invalidId)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/pois/{invalidId}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = problem.RootElement.GetProperty("errors");
        errors.TryGetProperty("id", out _).Should().BeTrue();
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

        var response = await client.GetAsync("/api/v1/pois/1");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var responseBody = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(responseBody);
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(500);
        problem.RootElement.GetProperty("title").GetString().Should().Be("An unexpected error occurred.");
        responseBody.Should().NotContain("Simulated database failure");
        responseBody.Should().NotContain("InvalidOperationException");
    }

    private static Task<SeedResult> SeedAsync(
        TripMateApiFactory factory,
        PointOfInterestStatus poiStatus = PointOfInterestStatus.Active) =>
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

            if (poiStatus != PointOfInterestStatus.Active)
            {
                context.Entry(poi).Property(p => p.Status).CurrentValue = poiStatus;
                await context.SaveChangesAsync();
            }

            return new SeedResult(user.Id, category.Id, poi.Id);
        });

    private sealed record SeedResult(long UserId, int CategoryId, long PoiId);
}