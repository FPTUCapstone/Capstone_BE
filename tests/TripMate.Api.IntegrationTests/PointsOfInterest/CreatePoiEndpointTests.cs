using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.PointsOfInterest;

[Collection(nameof(TripMateApiFactory))]
public class CreatePoiEndpointTests
{
    private static readonly DateTimeOffset SeedTime =
        new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Post_WithActiveAdministrator_ReturnsCreatedDtoWithoutPrematureLocation()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(
            seed.UserId,
            UserRole.Administrator);

        var response = await client.PostAsJsonAsync("/api/v1/admin/pois", new
        {
            name = "  Da Lat Flower Park  ",
            categoryId = seed.CategoryId,
            latitude = 11.9417554m,
            longitude = 108.4382784m,
            indoorOutdoor = "Mixed",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<PoiResponseDto>();
        body.Should().NotBeNull();
        body!.Id.Should().BePositive();
        body.Name.Should().Be("Da Lat Flower Park");
        body.Status.Should().Be(PointOfInterestStatus.Active);
        body.IndoorOutdoor.Should().Be(IndoorOutdoorType.Mixed);
        response.Headers.Location.Should().BeNull();
    }

    [Fact]
    public async Task Post_WithoutAuthentication_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"),
        });

        var response = await client.PostAsJsonAsync("/api/v1/admin/pois", ValidBody(1));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_WithNonAdministratorRole_ReturnsForbiddenProblemDetails()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory, UserRole.Traveler);
        using var client = factory.CreateAuthenticatedClient(seed.UserId, UserRole.Traveler);

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/pois",
            ValidBody(seed.CategoryId));

        await AssertForbiddenProblemDetailsAsync(response);
    }

    [Fact]
    public async Task Post_WithInactiveAdministrator_ReturnsForbiddenProblemDetails()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(
            factory,
            UserRole.Administrator,
            AccountStatus.Inactive);
        using var client = factory.CreateAuthenticatedClient(
            seed.UserId,
            UserRole.Administrator);

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/pois",
            ValidBody(seed.CategoryId));

        await AssertForbiddenProblemDetailsAsync(response);
    }

    [Fact]
    public async Task Post_WithMissingCategory_ReturnsNotFoundProblemDetails()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(
            seed.UserId,
            UserRole.Administrator);

        var response = await client.PostAsJsonAsync("/api/v1/admin/pois", ValidBody(999));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(PoiErrorCodes.ReferenceNotFound);
    }

    [Fact]
    public async Task Post_WithPossibleDuplicate_ReturnsConflictAndExistingPoiId()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory, includeExistingPoi: true);
        using var client = factory.CreateAuthenticatedClient(
            seed.UserId,
            UserRole.Administrator);

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/pois",
            ValidBody(seed.CategoryId));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(PoiErrorCodes.PossibleDuplicate);
        problem.RootElement.GetProperty("existingPoiId").GetInt64()
            .Should().Be(seed.ExistingPoiId);
    }

    [Fact]
    public async Task Post_WithoutRequiredCoordinates_ReturnsValidationProblemDetails()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(
            seed.UserId,
            UserRole.Administrator);

        var response = await client.PostAsJsonAsync("/api/v1/admin/pois", new
        {
            name = "Da Lat Flower Park",
            categoryId = seed.CategoryId,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType
            .Should().Be("application/problem+json");
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errors").TryGetProperty("Latitude", out _)
            .Should().BeTrue();
        problem.RootElement.GetProperty("errors").TryGetProperty("Longitude", out _)
            .Should().BeTrue();
    }

    [Fact]
    public async Task Post_WithNullOpeningHoursItem_ReturnsValidationProblemDetails()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(
            seed.UserId,
            UserRole.Administrator);

        var response = await client.PostAsJsonAsync("/api/v1/admin/pois", new
        {
            name = "Da Lat Flower Park",
            categoryId = seed.CategoryId,
            latitude = 11.941755m,
            longitude = 108.438278m,
            openingHours = new object?[] { null },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType
            .Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Post_WithOpeningHoursMissingDay_ReturnsValidationProblemDetails()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(
            seed.UserId,
            UserRole.Administrator);

        var response = await client.PostAsJsonAsync("/api/v1/admin/pois", new
        {
            name = "Da Lat Flower Park",
            categoryId = seed.CategoryId,
            latitude = 11.941755m,
            longitude = 108.438278m,
            openingHours = new[]
            {
                new
                {
                    openTime = "08:00:00",
                    closeTime = "17:00:00",
                    isClosed = false,
                },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType
            .Should().Be("application/problem+json");
    }

    private static object ValidBody(int categoryId) => new
    {
        name = "Da Lat Flower Park",
        categoryId,
        latitude = 11.941755m,
        longitude = 108.438278m,
    };

    private static async Task AssertForbiddenProblemDetailsAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType
            .Should().Be("application/problem+json");

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("status").GetInt32()
            .Should().Be((int)HttpStatusCode.Forbidden);
        problem.RootElement.GetProperty("title").GetString()
            .Should().Be(PoiErrorMessages.AdminAccessRequired);
        problem.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(PoiErrorCodes.AdminAccessRequired);
    }

    private static Task<SeedResult> SeedAsync(
        TripMateApiFactory factory,
        UserRole role = UserRole.Administrator,
        AccountStatus status = AccountStatus.Active,
        bool includeExistingPoi = false) =>
        factory.WithDbContextAsync(async context =>
        {
            var user = new User
            {
                Email = $"{Guid.NewGuid():N}@example.com",
                FullName = "API Test User",
                Role = role,
                Status = status,
                CreatedAtUtc = SeedTime,
                UpdatedAtUtc = SeedTime,
            };
            var category = PoiCategory.Create("Attraction", null);
            context.Users.Add(user);
            context.PoiCategories.Add(category);
            await context.SaveChangesAsync();

            long? existingPoiId = null;
            if (includeExistingPoi)
            {
                var existing = PointOfInterest.Create(
                    category,
                    "Da Lat Flower Park",
                    11.941755m,
                    108.438278m,
                    user.Id,
                    SeedTime);
                context.PointsOfInterest.Add(existing);
                await context.SaveChangesAsync();
                existingPoiId = existing.Id;
            }

            return new SeedResult(user.Id, category.Id, existingPoiId);
        });

    private sealed record SeedResult(long UserId, int CategoryId, long? ExistingPoiId);
}