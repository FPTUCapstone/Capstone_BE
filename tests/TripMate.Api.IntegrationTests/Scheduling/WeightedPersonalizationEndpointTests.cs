using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Scheduling;

/// <summary>
/// ADR-002 (G1): counterfactual evidence that WeightedUtility personalizes the itinerary through the real DI graph and
/// the shipped appsettings. Only <c>SchedulingGeneration:Csp:Mode</c> is overridden. Each pair keeps the POIs, the
/// request and the travel matrix fixed and changes one traveler preference. Assertions compare the two plans instead of
/// pinning exact stops, so they survive the G4 calibration.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class WeightedPersonalizationEndpointTests
{
    private const string CultureInterests = "[\"culture\",\"history\"]";
    private const int FullDayMinutes = 480;

    private static readonly PoiDefinition[] MixedPois =
    [
        new("Culture", "Culture-A", IndoorOutdoorType.Outdoor),
        new("Culture", "Culture-B", IndoorOutdoorType.Outdoor),
        new("Nature", "Nature-A", IndoorOutdoorType.Outdoor),
        new("Nature", "Nature-B", IndoorOutdoorType.Outdoor),
        new("Beach", "Beach-A", IndoorOutdoorType.Outdoor),
        new("Beach", "Beach-B", IndoorOutdoorType.Outdoor),
        new("Museum", "Museum-A", IndoorOutdoorType.Indoor),
        new("Theme park", "Themepark-A", IndoorOutdoorType.Outdoor),
    ];

    private static readonly PoiDefinition[] OutdoorPois =
    [
        new("Nature", "Nature-A", IndoorOutdoorType.Outdoor),
        new("Nature", "Nature-B", IndoorOutdoorType.Outdoor),
        new("Nature", "Nature-C", IndoorOutdoorType.Outdoor),
        new("Beach", "Beach-A", IndoorOutdoorType.Outdoor),
        new("Beach", "Beach-B", IndoorOutdoorType.Outdoor),
        new("Beach", "Beach-C", IndoorOutdoorType.Outdoor),
    ];

    [Fact]
    public async Task Interests_ChangeWhichPlacesAreVisited()
    {
        var culture = await GenerateVisitsAsync(MixedPois, Profile(CultureInterests));
        var nature = await GenerateVisitsAsync(MixedPois, Profile("[\"nature\",\"beach\"]"));

        culture.Should().NotBeEquivalentTo(nature);
        culture.Count(name => name.StartsWith("Culture-", StringComparison.Ordinal))
            .Should().BeGreaterThan(nature.Count(name => name.StartsWith("Culture-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SavedPreferences_ChangeTheColdStartPlan()
    {
        var coldStart = await GenerateVisitsAsync(MixedPois, profile: null);
        var culture = await GenerateVisitsAsync(MixedPois, Profile(CultureInterests));

        culture.Should().NotEqual(coldStart);
    }

    [Fact]
    public async Task Pace_OrdersTheNumberOfStops_RelaxedThenModerateThenFast()
    {
        var relaxed = await GenerateVisitsAsync(MixedPois, Profile(CultureInterests, TravelerPace.Relaxed), FullDayMinutes);
        var moderate = await GenerateVisitsAsync(MixedPois, Profile(CultureInterests, TravelerPace.Moderate), FullDayMinutes);
        var fast = await GenerateVisitsAsync(MixedPois, Profile(CultureInterests, TravelerPace.Fast), FullDayMinutes);

        relaxed.Count.Should().BeLessThan(fast.Count, "a relaxed traveler should get fewer stops than a fast one");
        moderate.Count.Should().BeInRange(relaxed.Count, fast.Count);
    }

    [Fact]
    public async Task RiskTolerance_LimitsConsecutiveExposedOutdoorStops()
    {
        var low = await GenerateVisitsAsync(OutdoorPois, Profile(null, TravelerPace.Fast, RiskToleranceLevel.Low), FullDayMinutes);
        var high = await GenerateVisitsAsync(OutdoorPois, Profile(null, TravelerPace.Fast, RiskToleranceLevel.High), FullDayMinutes);

        low.Count.Should().BeLessThan(high.Count, "a low-risk traveler should get fewer back-to-back exposed outdoor stops");
    }

    [Fact]
    public async Task TheSameProfile_GivesTheSamePlan_ForDifferentTravelers()
    {
        var first = await GenerateVisitsAsync(MixedPois, Profile(CultureInterests, TravelerPace.Fast), FullDayMinutes, userId: 42);
        var second = await GenerateVisitsAsync(MixedPois, Profile(CultureInterests, TravelerPace.Fast), FullDayMinutes, userId: 43);

        second.Should().Equal(first);
    }

    private static ProfileDefinition Profile(
        string? interests,
        TravelerPace? pace = null,
        RiskToleranceLevel? risk = null) => new(interests, pace, risk);

    private static async Task<IReadOnlyList<string>> GenerateVisitsAsync(
        PoiDefinition[] pois,
        ProfileDefinition? profile,
        int availableMinutes = 200,
        long userId = 42)
    {
        await using var parent = new TripMateApiFactory(dateTimeProviderFactory: _ => new FixedDateTimeProvider());
        await using var factory = parent.WithWebHostBuilder(builder =>
            builder.UseSetting("SchedulingGeneration:Csp:Mode", "WeightedUtility"));

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TestApiDbContext>();
            var categories = new Dictionary<string, PoiCategory>();
            for (var index = 0; index < pois.Length; index++)
            {
                var definition = pois[index];
                if (!categories.TryGetValue(definition.Category, out var category))
                {
                    category = PoiCategory.Create(definition.Category, null);
                    categories[definition.Category] = category;
                }

                var poi = PointOfInterest.Create(
                    category,
                    definition.Name,
                    16.0471m + ((index + 1) * 0.0001m),
                    108.2068m,
                    1,
                    DateTimeOffset.UtcNow,
                    indoorOutdoor: definition.IndoorOutdoor,
                    averageVisitDurationMinutes: 60);
                poi.ConfigurePlanningMetadata(60_000m, $"https://example.com/{definition.Name}", DateTimeOffset.UtcNow);
                poi.AddOpeningHour(PoiOpeningHour.Create(2, new TimeOnly(6, 0), new TimeOnly(21, 0), false));
                dbContext.PointsOfInterest.Add(poi);
            }

            if (profile is not null)
            {
                var traveler = TravelerProfile.CreateEmpty(userId, DateTimeOffset.UtcNow);
                traveler.ReplacePreferences(profile.Interests, null, profile.Pace, profile.Risk, null, DateTimeOffset.UtcNow);
                dbContext.TravelerProfiles.Add(traveler);
            }

            await dbContext.SaveChangesAsync();
        }

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.UserIdHeader, userId.ToString());
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.RoleHeader, nameof(UserRole.Traveler));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scheduling-requests")
        {
            Content = JsonContent.Create(new
            {
                startAt = "2026-10-20T08:00:00+07:00",
                timeZoneId = "Asia/Ho_Chi_Minh",
                startLatitude = 16.0544m,
                startLongitude = 108.2022m,
                explorationLatitude = 16.0471m,
                explorationLongitude = 108.2068m,
                endPoiId = (long?)null,
                returnToStart = true,
                availableMinutes,
                transportMode = "Motorbike",
                searchRadiusKm = 10m,
                budgetVnd = 800_000m,
                mandatoryPoiIds = Array.Empty<long>(),
                restPreference = "None",
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);

        using var payload = JsonDocument.Parse(body);
        return payload.RootElement.GetProperty("items")
            .EnumerateArray()
            .Where(IsVisit)
            .Select(item => item.GetProperty("poiName").GetString()!)
            .ToArray();
    }

    private static bool IsVisit(JsonElement item)
    {
        var kind = item.GetProperty("itemKind");
        return kind.ValueKind == JsonValueKind.String
            ? string.Equals(kind.GetString(), nameof(ItineraryItemKind.Visit), StringComparison.OrdinalIgnoreCase)
            : kind.GetInt32() == (int)ItineraryItemKind.Visit;
    }

    private sealed record PoiDefinition(string Category, string Name, IndoorOutdoorType IndoorOutdoor);

    private sealed record ProfileDefinition(string? Interests, TravelerPace? Pace, RiskToleranceLevel? Risk);

    private sealed class FixedDateTimeProvider : IDateTimeProvider
    {
        // The request starts at 08:00 ICT on 2026-10-20; the validation clock sits one hour earlier.
        public DateTimeOffset UtcNow => new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    }
}