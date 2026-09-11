using System.Net;
using System.Net.Http.Json;
using System.Text;
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
    public async Task Post_WithCaseInsensitiveIndoorOutdoor_ReturnsCanonicalEnumNames()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(
            seed.UserId,
            UserRole.Administrator);

        var response = await client.PostAsJsonAsync("/api/v1/admin/pois", new
        {
            name = "Case Insensitive Enum POI",
            categoryId = seed.CategoryId,
            latitude = 11.941755m,
            longitude = 108.438278m,
            indoorOutdoor = "mIxEd",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("indoorOutdoor").GetString().Should().Be("Mixed");
        body.RootElement.GetProperty("status").GetString().Should().Be("Active");
    }

    [Fact]
    public async Task Post_WithNullIndoorOutdoor_UsesOutdoorDefault()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(
            seed.UserId,
            UserRole.Administrator);

        var response = await client.PostAsJsonAsync("/api/v1/admin/pois", new
        {
            name = "Nullable Enum POI",
            categoryId = seed.CategoryId,
            latitude = 11.941755m,
            longitude = 108.438278m,
            indoorOutdoor = (string?)null,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("indoorOutdoor").GetString().Should().Be("Outdoor");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("\"Indoor, Outdoor\"")]
    [InlineData("\"Unknown\"")]
    public async Task Post_WithInvalidIndoorOutdoorWireValue_ReturnsValidationProblemDetails(
        string indoorOutdoorJson)
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(
            seed.UserId,
            UserRole.Administrator);
        var requestJson = $$"""
            {
              "name": "Invalid Enum POI",
              "categoryId": {{seed.CategoryId}},
              "latitude": 11.941755,
              "longitude": 108.438278,
              "indoorOutdoor": {{indoorOutdoorJson}}
            }
            """;

        var response = await client.PostAsync(
            "/api/v1/admin/pois",
            new StringContent(requestJson, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType
            .Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Post_WithPaddedFieldsAtSqlLimits_ReturnsNormalizedValues()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(
            seed.UserId,
            UserRole.Administrator);
        var expectedName = new string('n', PointOfInterest.NameMaxLength);
        var expectedAddress = new string('a', PointOfInterest.AddressMaxLength);
        var expectedDescription = new string('d', PointOfInterest.DescriptionMaxLength);

        var response = await client.PostAsJsonAsync("/api/v1/admin/pois", new
        {
            name = $"  {expectedName}  ",
            categoryId = seed.CategoryId,
            latitude = 11.941755m,
            longitude = 108.438278m,
            address = $"  {expectedAddress}  ",
            description = $"  {expectedDescription}  ",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<PoiResponseDto>();
        body.Should().NotBeNull();
        body!.Name.Should().Be(expectedName);
        body.Address.Should().Be(expectedAddress);
        body.Description.Should().Be(expectedDescription);

        var persistedValues = await factory.WithDbContextAsync(async dbContext =>
        {
            var poi = await dbContext.PointsOfInterest
                .AsNoTracking()
                .SingleAsync(item => item.Id == body.Id);
            return (poi.Name, poi.Address, poi.Description);
        });
        persistedValues.Name.Should().Be(expectedName);
        persistedValues.Address.Should().Be(expectedAddress);
        persistedValues.Description.Should().Be(expectedDescription);
    }

    [Fact]
    public async Task Post_WithWhitespaceOnlyOptionalText_PersistsNullValues()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(
            seed.UserId,
            UserRole.Administrator);

        var response = await client.PostAsJsonAsync("/api/v1/admin/pois", new
        {
            name = "Marble Mountains",
            categoryId = seed.CategoryId,
            latitude = 16.003892m,
            longitude = 108.264170m,
            address = new string(' ', PointOfInterest.AddressMaxLength + 1),
            description = new string(' ', PointOfInterest.DescriptionMaxLength + 1),
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<PoiResponseDto>();
        body.Should().NotBeNull();
        body!.Address.Should().BeNull();
        body.Description.Should().BeNull();

        var persistedValues = await factory.WithDbContextAsync(async dbContext =>
        {
            var poi = await dbContext.PointsOfInterest
                .AsNoTracking()
                .SingleAsync(item => item.Id == body.Id);
            return (poi.Address, poi.Description);
        });
        persistedValues.Address.Should().BeNull();
        persistedValues.Description.Should().BeNull();
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
        var errors = problem.RootElement.GetProperty("errors");
        errors.TryGetProperty("latitude", out _).Should().BeTrue();
        errors.TryGetProperty("longitude", out _).Should().BeTrue();
        errors.TryGetProperty("Latitude", out _).Should().BeFalse();
        errors.TryGetProperty("Longitude", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Post_WithoutRequiredName_ReturnsCamelCaseValidationProblemDetails()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(
            seed.UserId,
            UserRole.Administrator);

        var response = await client.PostAsJsonAsync("/api/v1/admin/pois", new
        {
            categoryId = seed.CategoryId,
            latitude = 11.941755m,
            longitude = 108.438278m,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType
            .Should().Be("application/problem+json");
        var responseBody = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(responseBody);
        var errors = problem.RootElement.GetProperty("errors");
        errors.TryGetProperty("name", out _)
            .Should().BeTrue("the validation body was {0}", responseBody);
        errors.TryGetProperty("Name", out _).Should().BeFalse();
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
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = problem.RootElement.GetProperty("errors");
        errors.TryGetProperty("openingHours[0].dayOfWeek", out _)
            .Should().BeTrue();
        errors.TryGetProperty("OpeningHours[0].DayOfWeek", out _)
            .Should().BeFalse();
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