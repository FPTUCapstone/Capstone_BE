using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Traveler.Preferences.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Traveler;

[Collection(nameof(TripMateApiFactory))]
public sealed class TravelerPreferencesEndpointTests
{
    private const string Route = "/api/v1/traveler/preferences";
    private const string OptionsRoute = "/api/v1/traveler/preferences/options";
    private static readonly DateTimeOffset SeedTime = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("GET", Route)]
    [InlineData("GET", OptionsRoute)]
    [InlineData("PUT", Route)]
    public async Task WithoutAuthentication_ReturnsUnauthorized(string method, string route)
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, method, route);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WithInvalidJwt_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");

        using var response = await client.GetAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(UserRole.TourOperator)]
    [InlineData(UserRole.Administrator)]
    public async Task WithNonTravelerRole_ReturnsForbidden(UserRole role)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(42, role);

        foreach (var (method, route) in AllEndpoints)
        {
            using var response = await SendAsync(client, method, route);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {route}");
        }
    }

    [Fact]
    public async Task WithLockedTraveler_ReturnsForbiddenProblemWithErrorCode()
    {
        await using var factory = new TripMateApiFactory();
        var userId = await SeedUserAsync(factory, AccountStatus.Locked);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.Traveler);

        using var response = await client.GetAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(TravelerPreferenceErrors.Forbidden);
    }

    [Fact]
    public async Task GetOptions_ReturnsCatalogueAndNoStore()
    {
        await using var factory = new TripMateApiFactory();
        var userId = await SeedUserAsync(factory, AccountStatus.Active, "Beach");
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.Traveler);

        using var response = await client.GetAsync(OptionsRoute);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await response.Content.ReadFromJsonAsync<TravelerPreferenceOptionsDto>();
        body!.Interests.Should().ContainSingle().Which.Key.Should().Be("Beach");
        body.PreferredTransportModes.Should().Contain("PublicTransit");
    }

    [Fact]
    public async Task PutThenGet_RoundTripsCanonicalValues()
    {
        await using var factory = new TripMateApiFactory();
        var userId = await SeedUserAsync(factory, AccountStatus.Active, "Beach");
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.Traveler);

        using var put = await client.PutAsJsonAsync(Route, new
        {
            interests = new[] { "beach" },
            preferredTransportMode = "motorbike",
            travelPace = "Relaxed",
            riskTolerance = (string?)null,
            foodPreferences = new[] { "NoRestriction" },
        });
        using var get = await client.GetAsync(Route);

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        get.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await get.Content.ReadFromJsonAsync<TravelerPreferencesDto>();
        body!.Interests.Should().Equal("Beach");
        body.PreferredTransportMode.Should().Be("Motorbike");
        body.TravelPace.Should().Be("Relaxed");
        body.RiskTolerance.Should().BeNull();
        body.FoodPreferences.Should().Equal("NoRestriction");
        body.UpdatedAtUtc.Should().NotBeNull();
        var raw = await get.Content.ReadAsStringAsync();
        raw.Should().NotContain("MSG");
        raw.Should().NotContain("defaultBudgetVnd");
    }

    [Theory]
    [InlineData("{\"preferredTransportMode\":\"Boat\"}", "preferredTransportMode", TravelerPreferenceErrors.InvalidOption)]
    [InlineData("{\"foodPreferences\":[\"NoRestriction\",\"Vegan\"]}", "foodPreferences", TravelerPreferenceErrors.FoodNoRestrictionExclusive)]
    public async Task Put_WithInvalidValue_ReturnsValidationProblemWithErrorCode(
        string json,
        string field,
        string errorCode)
    {
        await using var factory = new TripMateApiFactory();
        var userId = await SeedUserAsync(factory, AccountStatus.Active);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.Traveler);

        using var response = await client.PutAsync(
            Route,
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errors").GetProperty(field)[0].GetString()
            .Should().Be(errorCode);
        (await CountProfilesAsync(factory)).Should().Be(0);
    }

    [Fact]
    public async Task Put_WithUnknownInterest_ReturnsBadRequestWithFieldAndErrorCode()
    {
        await using var factory = new TripMateApiFactory();
        var userId = await SeedUserAsync(factory, AccountStatus.Active, "Beach");
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.Traveler);

        using var response = await client.PutAsJsonAsync(Route, new { interests = new[] { "Skydiving" } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(TravelerPreferenceErrors.InvalidOption);
        problem.RootElement.GetProperty("field").GetString()
            .Should().Be(TravelerPreferenceErrors.InterestsField);
        (await CountProfilesAsync(factory)).Should().Be(0);
    }

    private static readonly (string Method, string Route)[] AllEndpoints =
    [
        ("GET", Route),
        ("GET", OptionsRoute),
        ("PUT", Route),
    ];

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string route) =>
        method == "PUT"
            ? client.PutAsJsonAsync(route, new { })
            : client.GetAsync(route);

    private static Task<int> CountProfilesAsync(TripMateApiFactory factory) =>
        factory.WithDbContextAsync(context => context.TravelerProfiles.CountAsync());

    private static Task<long> SeedUserAsync(
        TripMateApiFactory factory,
        AccountStatus status,
        params string[] categories) =>
        factory.WithDbContextAsync(async context =>
        {
            var user = new User
            {
                Email = $"preferences-{Guid.NewGuid():N}@example.com",
                FullName = "Preference Traveler",
                Role = UserRole.Traveler,
                Status = status,
                CreatedAtUtc = SeedTime,
                UpdatedAtUtc = SeedTime,
            };
            context.Users.Add(user);
            context.PoiCategories.AddRange(categories.Select(name => PoiCategory.Create(name, null)));
            await context.SaveChangesAsync();
            return user.Id;
        });
}