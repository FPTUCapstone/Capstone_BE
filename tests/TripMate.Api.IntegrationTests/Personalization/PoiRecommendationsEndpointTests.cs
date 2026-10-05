using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Personalization.Recommendations;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Personalization;

[Collection(nameof(TripMateApiFactory))]
public sealed class PoiRecommendationsEndpointTests
{
    private const string Route = "/api/v1/poi-recommendations";
    private static readonly DateTimeOffset SeedTime =
        new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Post_WithoutAuthentication_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(Route, CreateValidRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_WithInvalidJwt_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "invalid-token");

        using var response = await client.PostAsJsonAsync(Route, CreateValidRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(UserRole.TourOperator)]
    [InlineData(UserRole.Administrator)]
    public async Task Post_WithNonTravelerRole_ReturnsForbidden(UserRole role)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(42, role);

        using var response = await client.PostAsJsonAsync(Route, CreateValidRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Post_WithValidTravelerRequest_ReturnsOkRecommendationResult()
    {
        await using var factory = new TripMateApiFactory();
        var poiIds = await SeedPlanningReadyPoisAsync(factory, 1);
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(Route, CreateValidRequest());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PoiRecommendationResultDto>();
        body.Should().NotBeNull();
        body!.TotalAvailable.Should().Be(1);
        body.Items.Should().ContainSingle().Which.PoiId.Should().Be(poiIds[0]);
    }

    [Theory]
    [InlineData(-90.1, 108.2068, 10, 10, "explorationLatitude")]
    [InlineData(90.1, 108.2068, 10, 10, "explorationLatitude")]
    [InlineData(16.0471, -180.1, 10, 10, "explorationLongitude")]
    [InlineData(16.0471, 180.1, 10, 10, "explorationLongitude")]
    [InlineData(16.0471, 108.2068, 0, 10, "searchRadiusKm")]
    [InlineData(16.0471, 108.2068, 51, 10, "searchRadiusKm")]
    [InlineData(16.0471, 108.2068, 10, 0, "limit")]
    [InlineData(16.0471, 108.2068, 10, 21, "limit")]
    public async Task Post_WithOutOfRangeValue_ReturnsValidationProblemDetails(
        double latitude,
        double longitude,
        int radiusKm,
        int limit,
        string expectedErrorKey)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(Route, new
        {
            explorationLatitude = latitude,
            explorationLongitude = longitude,
            searchRadiusKm = radiusKm,
            limit,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errors").TryGetProperty(expectedErrorKey, out _)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("explorationLatitude")]
    [InlineData("explorationLongitude")]
    [InlineData("searchRadiusKm")]
    public async Task Post_WithMissingRequiredField_ReturnsBadRequest(string omittedField)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);
        var request = new Dictionary<string, object?>
        {
            ["explorationLatitude"] = 16.0471m,
            ["explorationLongitude"] = 108.2068m,
            ["searchRadiusKm"] = 10,
            ["limit"] = 10,
        };
        request.Remove(omittedField);

        using var response = await client.PostAsJsonAsync(Route, request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Post_WithOmittedLimit_DefaultsToTenAndReportsAllAvailable()
    {
        await using var factory = new TripMateApiFactory();
        await SeedPlanningReadyPoisAsync(factory, 12);
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(Route, new
        {
            explorationLatitude = 16.0471m,
            explorationLongitude = 108.2068m,
            searchRadiusKm = 10,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PoiRecommendationResultDto>();
        body!.TotalAvailable.Should().Be(12);
        body.Items.Should().HaveCount(10);
    }

    [Fact]
    public async Task Post_WithNullLimit_IsAccepted()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(Route, CreateValidRequest(limit: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_WithNoEligibleCandidates_ReturnsOkWithEmptyItems()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(Route, CreateValidRequest());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("totalAvailable").GetInt32().Should().Be(0);
        body.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Post_ResponseUsesCamelCasePublicContractWithoutInternalPersonalizationFields()
    {
        await using var factory = new TripMateApiFactory();
        await SeedPlanningReadyPoisAsync(factory, 1);
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(Route, CreateValidRequest());

        var json = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(json);
        body.RootElement.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "totalAvailable",
            "items");
        body.RootElement.GetProperty("items")[0].EnumerateObject()
            .Select(property => property.Name)
            .Should().BeEquivalentTo(
                "poiId",
                "name",
                "categoryName",
                "thumbnailUrl",
                "distanceKm",
                "estimatedVisitCost",
                "averageRating",
                "recommendationReason");
        json.Should().NotContain("travelerUserId");
        json.Should().NotContain("tripMateBaseScore");
        json.Should().NotContain("behaviorAffinity");
        json.Should().NotContain("scenicScore");
        json.Should().NotContain("photoRating");
        json.Should().NotContain("interestTagsJson");
    }

    [Fact]
    public async Task Post_BodyTravelerUserIdCannotOverrideAuthenticatedIdentity()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedIdentityPreferenceScenarioAsync(factory);
        using var client = factory.CreateAuthenticatedClient(
            seed.AuthenticatedTravelerId,
            UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(Route, new
        {
            travelerUserId = seed.OtherTravelerId,
            explorationLatitude = 16.0471m,
            explorationLongitude = 108.2068m,
            searchRadiusKm = 10,
            limit = 10,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PoiRecommendationResultDto>();
        body!.Items.First().PoiId.Should().Be(seed.AuthenticatedTravelerPreferredPoiId);
    }

    [Fact]
    public async Task Post_BodyIdentityCannotSelectAnotherTravelersBehavior()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedOtherTravelerBehaviorScenarioAsync(factory);
        using var client = factory.CreateAuthenticatedClient(
            seed.AuthenticatedTravelerId,
            UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(Route, new
        {
            travelerUserId = seed.OtherTravelerId,
            explorationLatitude = 16.0471m,
            explorationLongitude = 108.2068m,
            searchRadiusKm = 10,
            limit = 10,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PoiRecommendationResultDto>();
        body!.Items.Select(item => item.PoiId)
            .Should().Equal(seed.NeutralFirstPoiId, seed.OtherTravelerLikedPoiId);
    }

    [Fact]
    public async Task Post_PerformsNoDatabaseWrites()
    {
        var interceptor = new CountingSaveChangesInterceptor();
        await using var factory = new TripMateApiFactory(saveChangesInterceptor: interceptor);
        await SeedPlanningReadyPoisAsync(factory, 1);
        interceptor.Reset();
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(Route, CreateValidRequest());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        interceptor.SaveCalls.Should().Be(0);
    }

    [Fact]
    public async Task Endpoint_IsExposedOnlyAtExactPostRoute()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);

        using var exactPost = await client.PostAsJsonAsync(Route, CreateValidRequest());
        using var exactGet = await client.GetAsync(Route);
        using var wrongPost = await client.PostAsJsonAsync(
            "/api/v1/poi-recommendation",
            CreateValidRequest());

        exactPost.StatusCode.Should().Be(HttpStatusCode.OK);
        exactGet.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        wrongPost.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static object CreateValidRequest(int? limit = 10) => new
    {
        explorationLatitude = 16.0471m,
        explorationLongitude = 108.2068m,
        searchRadiusKm = 10,
        limit,
    };

    private static Task<long[]> SeedPlanningReadyPoisAsync(
        TripMateApiFactory factory,
        int count) =>
        factory.WithDbContextAsync(async context =>
        {
            var category = PoiCategory.Create("Culture", null);
            context.PoiCategories.Add(category);
            await context.SaveChangesAsync();
            var pois = Enumerable.Range(1, count)
                .Select(index => CreatePlanningReadyPoi(category, $"POI {index}"))
                .ToArray();
            context.PointsOfInterest.AddRange(pois);
            await context.SaveChangesAsync();
            return pois.Select(poi => poi.Id).ToArray();
        });

    private static Task<IdentityPreferenceSeed> SeedIdentityPreferenceScenarioAsync(
        TripMateApiFactory factory) =>
        factory.WithDbContextAsync(async context =>
        {
            var authenticatedTraveler = CreateTraveler("authenticated");
            var otherTraveler = CreateTraveler("other");
            var nature = PoiCategory.Create("Nature", null);
            var culture = PoiCategory.Create("Culture", null);
            context.Users.AddRange(authenticatedTraveler, otherTraveler);
            context.PoiCategories.AddRange(nature, culture);
            await context.SaveChangesAsync();
            context.TravelerProfiles.AddRange(
                TravelerProfile.Create(authenticatedTraveler.Id, "[\"culture\"]", SeedTime),
                TravelerProfile.Create(otherTraveler.Id, "[\"nature\"]", SeedTime));
            var otherPreferred = CreatePlanningReadyPoi(nature, "Nature");
            var authenticatedPreferred = CreatePlanningReadyPoi(culture, "Culture");
            context.PointsOfInterest.AddRange(otherPreferred, authenticatedPreferred);
            await context.SaveChangesAsync();
            return new IdentityPreferenceSeed(
                authenticatedTraveler.Id,
                otherTraveler.Id,
                authenticatedPreferred.Id);
        });

    private static Task<OtherTravelerBehaviorSeed> SeedOtherTravelerBehaviorScenarioAsync(
        TripMateApiFactory factory) =>
        factory.WithDbContextAsync(async context =>
        {
            var authenticatedTraveler = CreateTraveler("behavior-authenticated");
            var otherTraveler = CreateTraveler("behavior-other");
            var category = PoiCategory.Create("Culture", null);
            context.Users.AddRange(authenticatedTraveler, otherTraveler);
            context.PoiCategories.Add(category);
            await context.SaveChangesAsync();
            var neutralFirst = CreatePlanningReadyPoi(category, "Neutral first");
            var otherTravelerLiked = CreatePlanningReadyPoi(category, "Other traveler liked");
            context.PointsOfInterest.AddRange(neutralFirst, otherTravelerLiked);
            await context.SaveChangesAsync();
            context.RecommendationBehaviorEvents.Add(RecommendationBehaviorEvent.Create(
                otherTraveler.Id,
                otherTravelerLiked.Id,
                itineraryId: null,
                RecommendationEventType.Like,
                originalPosition: null,
                newPosition: null,
                wasMandatory: null,
                RecommendationCaptureSource.Explore,
                SeedTime,
                Guid.NewGuid()));
            await context.SaveChangesAsync();
            return new OtherTravelerBehaviorSeed(
                authenticatedTraveler.Id,
                otherTraveler.Id,
                neutralFirst.Id,
                otherTravelerLiked.Id);
        });

    private static User CreateTraveler(string discriminator) => new()
    {
        Email = $"{discriminator}-{Guid.NewGuid():N}@example.com",
        FullName = "Recommendation Traveler",
        Role = UserRole.Traveler,
        Status = AccountStatus.Active,
        CreatedAtUtc = SeedTime,
        UpdatedAtUtc = SeedTime,
    };

    private static PointOfInterest CreatePlanningReadyPoi(
        PoiCategory category,
        string name)
    {
        var poi = PointOfInterest.Create(
            category,
            name,
            16.0471m,
            108.2068m,
            1,
            SeedTime);
        poi.ConfigurePlanningMetadata(
            60_000m,
            $"https://example.com/{Guid.NewGuid():N}",
            SeedTime);
        poi.AddOpeningHour(PoiOpeningHour.Create(
            1,
            new TimeOnly(8, 0),
            new TimeOnly(17, 0),
            false));
        return poi;
    }

    private sealed record IdentityPreferenceSeed(
        long AuthenticatedTravelerId,
        long OtherTravelerId,
        long AuthenticatedTravelerPreferredPoiId);

    private sealed record OtherTravelerBehaviorSeed(
        long AuthenticatedTravelerId,
        long OtherTravelerId,
        long NeutralFirstPoiId,
        long OtherTravelerLikedPoiId);

    private sealed class CountingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public int SaveCalls { get; private set; }

        public void Reset() => SaveCalls = 0;

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            SaveCalls++;
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}