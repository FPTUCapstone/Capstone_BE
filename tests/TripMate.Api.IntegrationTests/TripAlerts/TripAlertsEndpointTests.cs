using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Api.Controllers.V1.Requests;
using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Application.Features.TripAlerts.Weather;
using TripMate.Application.Features.TripAlerts.WeatherAdvisories;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.TripAlerts;

/// <summary>
/// Integration tests for UC-14 Real-Time Monitoring &amp; Trip Alerts endpoints:
/// - POST /api/v1/navigation-sessions/{sessionId}/deviations
/// - GET  /api/v1/navigation-sessions/{sessionId}/alerts
/// - PUT  /api/v1/navigation-sessions/{sessionId}/alerts/{incidentId}/acknowledgement
/// - GET  /api/v1/navigation-sessions/{sessionId}/weather-advisories
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class TripAlertsEndpointTests
{
    private static readonly DateTimeOffset TestBaseTime = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    #region 1. Authentication & IDOR (BOLA) Tests

    [Fact]
    public async Task Anonymous_AllEndpoints_ReturnUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var deviationResponse = await client.PostAsJsonAsync(
            "/api/v1/navigation-sessions/1/deviations",
            new ReportDeviationRequest(Guid.NewGuid(), TestBaseTime, 1));
        var alertsResponse = await client.GetAsync("/api/v1/navigation-sessions/1/alerts");
        var ackResponse = await client.PutAsync(
            "/api/v1/navigation-sessions/1/alerts/1/acknowledgement",
            content: null);
        var weatherResponse = await client.GetAsync("/api/v1/navigation-sessions/1/weather-advisories");

        deviationResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        alertsResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        ackResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        weatherResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NotSessionOwner_AllEndpoints_ReturnForbidden_IDOR()
    {
        await using var factory = new TripMateApiFactory();
        SeedData seed = null!;
        await factory.WithDbContextAsync(async db => seed = await SeedScenarioAsync(db));

        // Use client of OtherTraveler (attacker trying to access Traveler's session)
        using var otherClient = factory.CreateAuthenticatedClient(seed.OtherTravelerId, UserRole.Traveler);

        var deviationResponse = await otherClient.PostAsJsonAsync(
            $"/api/v1/navigation-sessions/{seed.SessionId}/deviations",
            new ReportDeviationRequest(Guid.NewGuid(), TestBaseTime, seed.ItemIds[0]));
        var alertsResponse = await otherClient.GetAsync($"/api/v1/navigation-sessions/{seed.SessionId}/alerts");
        var ackResponse = await otherClient.PutAsync(
            $"/api/v1/navigation-sessions/{seed.SessionId}/alerts/{seed.ExistingIncidentId}/acknowledgement",
            content: null);
        var weatherResponse = await otherClient.GetAsync(
            $"/api/v1/navigation-sessions/{seed.SessionId}/weather-advisories");

        deviationResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(deviationResponse)).Should().Be(TripAlertErrorCodes.AccessDenied);

        alertsResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(alertsResponse)).Should().Be(TripAlertErrorCodes.AccessDenied);

        ackResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(ackResponse)).Should().Be(TripAlertErrorCodes.AccessDenied);

        weatherResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(weatherResponse)).Should().Be(TripAlertErrorCodes.AccessDenied);
    }

    [Fact]
    public async Task SessionNotFound_ReturnsNotFound()
    {
        await using var factory = new TripMateApiFactory();
        SeedData seed = null!;
        await factory.WithDbContextAsync(async db => seed = await SeedScenarioAsync(db));

        using var client = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);
        const long nonExistentSessionId = 999_999_999;

        var deviationResponse = await client.PostAsJsonAsync(
            $"/api/v1/navigation-sessions/{nonExistentSessionId}/deviations",
            new ReportDeviationRequest(Guid.NewGuid(), TestBaseTime, seed.ItemIds[0]));
        var alertsResponse = await client.GetAsync($"/api/v1/navigation-sessions/{nonExistentSessionId}/alerts");
        var ackResponse = await client.PutAsync(
            $"/api/v1/navigation-sessions/{nonExistentSessionId}/alerts/1/acknowledgement",
            content: null);
        var weatherResponse = await client.GetAsync(
            $"/api/v1/navigation-sessions/{nonExistentSessionId}/weather-advisories");

        deviationResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        alertsResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ackResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        weatherResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region 2. Report Deviation Tests (POST /deviations)

    [Fact]
    public async Task ReportDeviation_ValidRequest_CreatesIncidentAndReturnsAlertResponse()
    {
        await using var factory = new TripMateApiFactory();
        SeedData seed = null!;
        await factory.WithDbContextAsync(async db => seed = await SeedScenarioAsync(db));

        using var client = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);
        var eventId = Guid.NewGuid();
        var targetItemId = seed.ItemIds[1]; // Pending item without existing incident

        var response = await client.PostAsJsonAsync(
            $"/api/v1/navigation-sessions/{seed.SessionId}/deviations",
            new ReportDeviationRequest(eventId, TestBaseTime, targetItemId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var alert = await response.Content.ReadFromJsonAsync<TripAlertResponse>();
        alert.Should().NotBeNull();
        alert!.IncidentId.Should().BeGreaterThan(0);
        alert.Type.Should().Be(Incident.RouteDeviationType);
        alert.Severity.Should().Be(TripAlertResponse.WarningSeverity);
        alert.MessageCode.Should().Be(TripAlertResponse.RouteDeviationMessageCode);
        alert.IsRead.Should().BeFalse();
        alert.AffectedItem.Should().NotBeNull();
        alert.AffectedItem!.ItemId.Should().Be(targetItemId);
    }

    [Fact]
    public async Task ReportDeviation_ReplaySameEventId_ReturnsSameIncident_Idempotent()
    {
        await using var factory = new TripMateApiFactory();
        SeedData seed = null!;
        await factory.WithDbContextAsync(async db => seed = await SeedScenarioAsync(db));

        using var client = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);
        var eventId = Guid.NewGuid();
        var targetItemId = seed.ItemIds[1];

        // First call
        var firstResponse = await client.PostAsJsonAsync(
            $"/api/v1/navigation-sessions/{seed.SessionId}/deviations",
            new ReportDeviationRequest(eventId, TestBaseTime, targetItemId));
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstAlert = await firstResponse.Content.ReadFromJsonAsync<TripAlertResponse>();

        // Replay with identical eventId & targetItemId
        var replayResponse = await client.PostAsJsonAsync(
            $"/api/v1/navigation-sessions/{seed.SessionId}/deviations",
            new ReportDeviationRequest(eventId, TestBaseTime, targetItemId));
        replayResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var replayAlert = await replayResponse.Content.ReadFromJsonAsync<TripAlertResponse>();

        replayAlert.Should().NotBeNull();
        replayAlert!.IncidentId.Should().Be(firstAlert!.IncidentId);
    }

    [Fact]
    public async Task ReportDeviation_SameEventIdDifferentTarget_ReturnsConflict409()
    {
        await using var factory = new TripMateApiFactory();
        SeedData seed = null!;
        await factory.WithDbContextAsync(async db => seed = await SeedScenarioAsync(db));

        using var client = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);
        var eventId = Guid.NewGuid();

        // First call for target 1
        var firstResponse = await client.PostAsJsonAsync(
            $"/api/v1/navigation-sessions/{seed.SessionId}/deviations",
            new ReportDeviationRequest(eventId, TestBaseTime, seed.ItemIds[1]));
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Replay same eventId with different target 2
        var conflictResponse = await client.PostAsJsonAsync(
            $"/api/v1/navigation-sessions/{seed.SessionId}/deviations",
            new ReportDeviationRequest(eventId, TestBaseTime, seed.ItemIds[2]));

        conflictResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorCodeAsync(conflictResponse)).Should().Be(TripAlertErrorCodes.EventPayloadMismatch);
    }

    [Fact]
    public async Task ReportDeviation_OpenIncidentOnTarget_ReturnsExistingOpenIncident()
    {
        await using var factory = new TripMateApiFactory();
        SeedData seed = null!;
        await factory.WithDbContextAsync(async db => seed = await SeedScenarioAsync(db));

        using var client = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);
        // seed.ItemIds[0] already has an open RouteDeviation incident created during seed
        var newEventId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/navigation-sessions/{seed.SessionId}/deviations",
            new ReportDeviationRequest(newEventId, TestBaseTime, seed.ItemIds[0]));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var alert = await response.Content.ReadFromJsonAsync<TripAlertResponse>();
        alert.Should().NotBeNull();
        alert!.IncidentId.Should().Be(seed.ExistingIncidentId);
    }

    [Fact]
    public async Task ReportDeviation_WithinCooldown5Minutes_ReturnsTooManyRequests429()
    {
        await using var factory = new TripMateApiFactory();
        SeedData seed = null!;
        await factory.WithDbContextAsync(async db => seed = await SeedScenarioAsync(db, withExistingIncident: false));

        using var client = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);

        // First deviation for target 0 -> 200 OK
        var firstResponse = await client.PostAsJsonAsync(
            $"/api/v1/navigation-sessions/{seed.SessionId}/deviations",
            new ReportDeviationRequest(Guid.NewGuid(), DateTimeOffset.UtcNow, seed.ItemIds[0]));
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second deviation for different target within 5 minutes -> 429 Too Many Requests
        var response = await client.PostAsJsonAsync(
            $"/api/v1/navigation-sessions/{seed.SessionId}/deviations",
            new ReportDeviationRequest(Guid.NewGuid(), DateTimeOffset.UtcNow, seed.ItemIds[1]));

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ReadErrorCodeAsync(response)).Should().Be(TripAlertErrorCodes.DeviationCooldown);
    }

    [Fact]
    public async Task ReportDeviation_NonUtcOffset_ReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        SeedData seed = null!;
        await factory.WithDbContextAsync(async db => seed = await SeedScenarioAsync(db));

        using var client = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);
        var nonUtcTime = new DateTimeOffset(2026, 10, 22, 15, 0, 0, TimeSpan.FromHours(7));

        var response = await client.PostAsJsonAsync(
            $"/api/v1/navigation-sessions/{seed.SessionId}/deviations",
            new ReportDeviationRequest(Guid.NewGuid(), nonUtcTime, seed.ItemIds[1]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("observedAtUtc must be UTC");
    }

    #endregion

    #region 3. End-to-End Demo Loop: Start -> Deviation -> List Alerts -> Acknowledge -> Verify

    [Fact]
    public async Task EndToEnd_DemoLoop_ReportDeviation_ListAlerts_Acknowledge_VerifiesIsRead()
    {
        await using var factory = new TripMateApiFactory();
        SeedData seed = null!;
        // Seed clean session without pre-existing incidents so cooldown is not active
        await factory.WithDbContextAsync(async db => seed = await SeedScenarioAsync(db, withExistingIncident: false));

        using var client = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);

        // Step 1: Initial alerts list should be empty
        var initialListResponse = await client.GetAsync($"/api/v1/navigation-sessions/{seed.SessionId}/alerts");
        initialListResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var initialAlerts = await initialListResponse.Content.ReadFromJsonAsync<IReadOnlyCollection<TripAlertResponse>>();
        initialAlerts.Should().BeEmpty();

        // Step 2: Report deviation on item 0
        var eventId = Guid.NewGuid();
        var reportResponse = await client.PostAsJsonAsync(
            $"/api/v1/navigation-sessions/{seed.SessionId}/deviations",
            new ReportDeviationRequest(eventId, TestBaseTime, seed.ItemIds[0]));
        reportResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var reportedAlert = await reportResponse.Content.ReadFromJsonAsync<TripAlertResponse>();
        reportedAlert.Should().NotBeNull();
        reportedAlert!.IsRead.Should().BeFalse();
        var incidentId = reportedAlert.IncidentId;

        // Step 3: GET alerts should now contain the new unread alert
        var afterReportResponse = await client.GetAsync($"/api/v1/navigation-sessions/{seed.SessionId}/alerts");
        afterReportResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var alertsAfterReport = await afterReportResponse.Content.ReadFromJsonAsync<IReadOnlyCollection<TripAlertResponse>>();
        alertsAfterReport.Should().HaveCount(1);
        alertsAfterReport!.Single().IncidentId.Should().Be(incidentId);
        alertsAfterReport.Single().IsRead.Should().BeFalse();

        // Step 4: Acknowledge the alert -> Expect 204 No Content
        var ackResponse = await client.PutAsync(
            $"/api/v1/navigation-sessions/{seed.SessionId}/alerts/{incidentId}/acknowledgement",
            content: null);
        ackResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Step 5: Calling acknowledgement again should be idempotent -> 204 No Content
        var ackAgainResponse = await client.PutAsync(
            $"/api/v1/navigation-sessions/{seed.SessionId}/alerts/{incidentId}/acknowledgement",
            content: null);
        ackAgainResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Step 6: GET alerts should now reflect isRead == true
        var finalListResponse = await client.GetAsync($"/api/v1/navigation-sessions/{seed.SessionId}/alerts");
        finalListResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var finalAlerts = await finalListResponse.Content.ReadFromJsonAsync<IReadOnlyCollection<TripAlertResponse>>();
        finalAlerts.Should().HaveCount(1);
        finalAlerts!.Single().IncidentId.Should().Be(incidentId);
        finalAlerts.Single().IsRead.Should().BeTrue();
    }

    #endregion

    #region 4. Weather Advisories Tests (GET /weather-advisories)

    [Fact]
    public async Task GetWeatherAdvisories_ProviderDisabled_ReturnsEmptyList200()
    {
        // By default Tomorrow:Enabled is false in test environment
        await using var factory = new TripMateApiFactory(
            dateTimeProviderFactory: _ => new FixedClock(TestBaseTime));
        SeedData seed = null!;
        await factory.WithDbContextAsync(async db => seed = await SeedScenarioAsync(db));

        using var client = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);

        var response = await client.GetAsync($"/api/v1/navigation-sessions/{seed.SessionId}/weather-advisories");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var advisories = await response.Content.ReadFromJsonAsync<IReadOnlyCollection<WeatherAdvisoryResponse>>();
        advisories.Should().NotBeNull();
        advisories.Should().BeEmpty();
    }

    [Fact]
    public async Task GetWeatherAdvisories_OutdoorItemWithModerateRain_ReturnsAdvisory()
    {
        var stubSnapshot = new TripWeatherSnapshot(
            TestBaseTime.AddHours(1),
            5.0, // 5 mm/hr >= 2.5 mm/hr threshold -> classifies as Advisory
            Thunderstorm: false,
            Condition: "Moderate rain",
            OfficialAlerts: Array.Empty<TripWeatherOfficialAlert>());

        await using var factory = new TripMateApiFactory(
            dateTimeProviderFactory: _ => new FixedClock(TestBaseTime),
            configureTestServices: services =>
        {
            services.RemoveAll<ITripWeatherProvider>();
            services.AddSingleton<ITripWeatherProvider>(new StubWeatherProvider(stubSnapshot));
        });

        SeedData seed = null!;
        await factory.WithDbContextAsync(async db => seed = await SeedScenarioAsync(db));

        using var client = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);

        var response = await client.GetAsync($"/api/v1/navigation-sessions/{seed.SessionId}/weather-advisories");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var advisories = await response.Content.ReadFromJsonAsync<IReadOnlyCollection<WeatherAdvisoryResponse>>();
        advisories.Should().NotBeNull();
        advisories.Should().NotBeEmpty();

        var advisory = advisories!.First();
        advisory.Severity.Should().Be(WeatherAdvisoryResponse.InfoSeverity);
        advisory.MessageCode.Should().Be(WeatherAdvisoryResponse.ModerateRainMessageCode);
        advisory.RainIntensityMmPerHour.Should().Be(5.0);
        advisory.Condition.Should().Be("Moderate rain");
    }

    [Fact]
    public async Task MalformedPayload_ReturnsBadRequest_WithoutStackTrace()
    {
        await using var factory = new TripMateApiFactory();
        SeedData seed = null!;
        await factory.WithDbContextAsync(async db => seed = await SeedScenarioAsync(db));

        using var client = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);

        using var malformedContent = new StringContent(
            "{\"eventId\": \"not-a-guid\", \"detectedAt\": \"invalid-date\",}",
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync(
            $"/api/v1/navigation-sessions/{seed.SessionId}/deviations",
            malformedContent);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ToLowerInvariant().Should().NotContain("stacktrace");
        body.Should().NotContain("at TripMate");
    }

    [Fact]
    public async Task UnsupportedContentType_ReturnsUnsupportedMediaType()
    {
        await using var factory = new TripMateApiFactory();
        SeedData seed = null!;
        await factory.WithDbContextAsync(async db => seed = await SeedScenarioAsync(db));

        using var client = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);

        using var textContent = new StringContent(
            "plain text body",
            System.Text.Encoding.UTF8,
            "text/plain");

        var response = await client.PostAsync(
            $"/api/v1/navigation-sessions/{seed.SessionId}/deviations",
            textContent);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        var body = await response.Content.ReadAsStringAsync();
        body.ToLowerInvariant().Should().NotContain("stacktrace");
    }

    #endregion

    #region Helpers & Seeding

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        try
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (json.TryGetProperty("errorCode", out var codeProperty))
            {
                return codeProperty.GetString();
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private static async Task<SeedData> SeedScenarioAsync(
        IApplicationDbContext db,
        bool withExistingIncident = true)
    {
        var now = TestBaseTime;
        var suffix = Guid.NewGuid().ToString("N");

        var traveler = new User
        {
            Email = $"uc14-traveler-{suffix}@example.com",
            FullName = "UC14 Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var otherTraveler = new User
        {
            Email = $"uc14-other-{suffix}@example.com",
            FullName = "UC14 Other Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.Users.AddRange(traveler, otherTraveler);

        var category = PoiCategory.Create($"Category-{suffix}", null);
        db.PoiCategories.Add(category);
        await db.SaveChangesAsync(CancellationToken.None);

        var poiNames = new[] { "Marble Mountains", "Dragon Bridge", "My Khe Beach" };
        var pois = poiNames.Select((name, i) => PointOfInterest.Create(
            category,
            name,
            16.0m + (i * 0.01m),
            108.2m + (i * 0.01m),
            traveler.Id,
            now,
            indoorOutdoor: IndoorOutdoorType.Outdoor)).ToArray();
        foreach (var poi in pois)
            poi.ReviewIndoorOutdoor(IndoorOutdoorType.Outdoor, now);
        db.PointsOfInterest.AddRange(pois);
        await db.SaveChangesAsync(CancellationToken.None);

        var itinerary = Itinerary.CreateManual(traveler.Id, "UC14 Tour", Itinerary.ActiveStatus, now);
        for (var i = 0; i < pois.Length; i++)
        {
            itinerary.AddItem(ItineraryItem.CreateVisit(
                i + 1,
                pois[i].Id,
                now.AddHours(i + 1),
                now.AddHours(i + 2),
                i == 0,
                0m,
                $"Stop {i + 1}"));
        }
        db.Itineraries.Add(itinerary);
        await db.SaveChangesAsync(CancellationToken.None);

        var itineraryItems = itinerary.Items.OrderBy(item => item.SequenceNo).ToArray();

        var session = TripSession.Start(
            itinerary.Id,
            itinerary.Id,
            traveler.Id,
            Guid.NewGuid(),
            now,
            now.AddHours(6),
            itineraryItems.Select((item, i) => TripSessionItem.Snapshot(
                item.Id,
                item.SequenceNo,
                pois[i].Id,
                pois[i].Name,
                pois[i].Latitude,
                pois[i].Longitude,
                item.PlannedArrivalUtc!.Value,
                item.PlannedDepartureUtc!.Value,
                true)));
        db.TripSessions.Add(session);
        await db.SaveChangesAsync(CancellationToken.None);

        long existingIncidentId = 0;
        if (withExistingIncident)
        {
            var firstItem = itineraryItems[0];
            var incident = Incident.CreateTripAlert(
                session,
                Incident.RouteDeviationType,
                $"deviation:{firstItem.Id}",
                now,
                firstItem.Id);
            db.Incidents.Add(incident);
            await db.SaveChangesAsync(CancellationToken.None);

            var notification = new Notification
            {
                UserId = traveler.Id,
                Channel = NotificationChannel.Push,
                Type = Notification.TripAlertType,
                Title = "Route deviation detected",
                Body = "You are deviating from your planned route.",
                RelatedEntityType = Notification.IncidentRelatedEntityType,
                RelatedEntityId = incident.Id,
                Status = NotificationStatus.Pending,
                CreatedAtUtc = now,
            };
            db.Notifications.Add(notification);
            await db.SaveChangesAsync(CancellationToken.None);

            existingIncidentId = incident.Id;
        }

        return new SeedData(
            traveler.Id,
            otherTraveler.Id,
            session.Id,
            itineraryItems.Select(item => item.Id).ToArray(),
            existingIncidentId);
    }

    private sealed record SeedData(
        long TravelerId,
        long OtherTravelerId,
        long SessionId,
        long[] ItemIds,
        long ExistingIncidentId);

    private sealed class StubWeatherProvider(TripWeatherSnapshot? snapshot) : ITripWeatherProvider
    {
        public Task<TripWeatherSnapshot?> GetAsync(
            WeatherForecastRequest request,
            CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }

    #endregion
}