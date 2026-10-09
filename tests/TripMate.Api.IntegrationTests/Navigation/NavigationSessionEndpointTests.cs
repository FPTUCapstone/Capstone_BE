using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Navigation;

[Collection(nameof(TripMateApiFactory))]
public sealed class NavigationSessionEndpointTests
{
    private const string FirstKey = "11111111-1111-1111-1111-111111111111";
    private const string SecondKey = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task Lifecycle_FlexibleOrderExploringSkipRevisitAndFinish()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(7, UserRole.Traveler);

        using var start = await StartAsync(client, seed.ItineraryId, FirstKey);
        start.StatusCode.Should().Be(HttpStatusCode.Created);
        using var startedJson = await ReadAsync(start);
        var sessionId = startedJson.RootElement.GetProperty("sessionId").GetInt64();
        start.Headers.Location!.ToString().Should().Be($"/api/v1/navigation-sessions/{sessionId}");
        var basePath = $"/api/v1/navigation-sessions/{sessionId}";

        // The session start is the earliest device time the Backend accepts.
        var thirdReachedAt = startedJson.RootElement.GetProperty("startedAtUtc").GetDateTimeOffset();
        using var reachThird = await client.PutAsJsonAsync(
            $"{basePath}/reached-items/{seed.ItemIds[2]}",
            new { occurredAtUtc = thirdReachedAt });
        reachThird.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var json = await ReadAsync(reachThird))
        {
            json.RootElement.GetProperty("state").GetString().Should().Be(TripSession.ExploringState);
            json.RootElement.GetProperty("exploringItemId").GetInt64().Should().Be(seed.ItemIds[2]);
            json.RootElement.GetProperty("nextItemId").GetInt64().Should().Be(seed.ItemIds[0]);
            Item(json, 2).GetProperty("reachedAtUtc").GetDateTimeOffset().Should().Be(thirdReachedAt);
        }

        using var depart = await client.PutAsJsonAsync(
            $"{basePath}/state",
            new { state = TripSession.NavigatingState });
        depart.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(depart)).RootElement.GetProperty("state").GetString()
            .Should().Be(TripSession.NavigatingState);

        using var skipSecond = await client.PutAsync($"{basePath}/skipped-items/{seed.ItemIds[1]}", content: null);
        skipSecond.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var json = await ReadAsync(skipSecond))
        {
            Item(json, 1).GetProperty("status").GetString().Should().Be(TripSessionItem.SkippedStatus);
        }

        using var reachFirst = await client.PutAsync($"{basePath}/reached-items/{seed.ItemIds[0]}", content: null);
        reachFirst.StatusCode.Should().Be(HttpStatusCode.OK);
        using var revisitSecond = await client.PutAsync($"{basePath}/reached-items/{seed.ItemIds[1]}", content: null);
        revisitSecond.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var json = await ReadAsync(revisitSecond))
        {
            Item(json, 1).GetProperty("status").GetString().Should().Be(TripSessionItem.ReachedStatus);
            json.RootElement.GetProperty("nextItemId").ValueKind.Should().Be(JsonValueKind.Null);
        }

        using var finish = await client.PutAsync($"{basePath}/completion", content: null);
        using var finishReplay = await client.PutAsync($"{basePath}/completion", content: null);
        finish.StatusCode.Should().Be(HttpStatusCode.OK);
        finishReplay.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var json = await ReadAsync(finishReplay))
        {
            json.RootElement.GetProperty("state").GetString().Should().Be(TripSession.CompletedState);
            json.RootElement.GetProperty("completionReason").GetString()
                .Should().Be(TripSession.RouteFinishedReason);
        }

        await factory.WithDbContextAsync(async db =>
        {
            // Started, reached (Exploring), departed, reached (Exploring), finished.
            (await db.Set<TripStateHistory>().CountAsync()).Should().Be(5);
            return true;
        });
    }

    [Fact]
    public async Task ActiveSession_ProvidesResumeContractAndCanBeStoppedIdempotently()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var owner = factory.CreateAuthenticatedClient(7, UserRole.Traveler);

        using var first = await StartAsync(owner, seed.ItineraryId, FirstKey);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var sessionId = (await ReadAsync(first)).RootElement.GetProperty("sessionId").GetInt64();

        using var conflict = await StartAsync(owner, seed.ItineraryId, SecondKey);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        conflict.Headers.Location!.ToString().Should().Be($"/api/v1/navigation-sessions/{sessionId}");
        using (var json = await ReadAsync(conflict))
        {
            json.RootElement.GetProperty("activeSessionId").GetInt64().Should().Be(sessionId);
            json.RootElement.GetProperty("errorCode").GetString().Should().Be("navigation.active_session_exists");
        }

        using var resume = await owner.GetAsync("/api/v1/navigation-sessions?state=Open");
        resume.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(resume)).RootElement.GetArrayLength().Should().Be(1);

        using var unsupported = await owner.GetAsync("/api/v1/navigation-sessions?state=Navigating");
        unsupported.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync(unsupported)).RootElement.GetProperty("errorCode").GetString()
            .Should().Be("navigation.unsupported_state_filter");

        using var foreign = factory.CreateAuthenticatedClient(8, UserRole.Traveler);
        using var forbidden = await foreign.GetAsync($"/api/v1/navigation-sessions/{sessionId}");
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var stopped = await owner.PutAsync($"/api/v1/navigation-sessions/{sessionId}/completion", content: null);
        stopped.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(stopped)).RootElement.GetProperty("completionReason").GetString()
            .Should().Be(TripSession.TravelerStoppedReason);

        using var afterStop = await owner.GetAsync("/api/v1/navigation-sessions?state=Open");
        (await ReadAsync(afterStop)).RootElement.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ProgressErrors_UseTheDocumentedStatusAndErrorCodes()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(7, UserRole.Traveler);
        using var start = await StartAsync(client, seed.ItineraryId, FirstKey);
        var sessionId = (await ReadAsync(start)).RootElement.GetProperty("sessionId").GetInt64();
        var basePath = $"/api/v1/navigation-sessions/{sessionId}";

        await ExpectErrorAsync(
            await client.PutAsync("/api/v1/navigation-sessions/999999/reached-items/1", content: null),
            HttpStatusCode.NotFound,
            "navigation.session_not_found");
        await ExpectErrorAsync(
            await client.PutAsync($"{basePath}/reached-items/999999", content: null),
            HttpStatusCode.UnprocessableEntity,
            "navigation.item_not_navigable");

        (await client.PutAsync($"{basePath}/reached-items/{seed.ItemIds[0]}", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await ExpectErrorAsync(
            await client.PutAsync($"{basePath}/skipped-items/{seed.ItemIds[0]}", content: null),
            HttpStatusCode.Conflict,
            "navigation.item_already_reached");

        using var invalidState = await client.PutAsJsonAsync($"{basePath}/state", new { state = "Completed" });
        invalidState.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await client.PutAsync($"{basePath}/completion", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await ExpectErrorAsync(
            await client.PutAsync($"{basePath}/reached-items/{seed.ItemIds[1]}", content: null),
            HttpStatusCode.Conflict,
            "navigation.session_completed");
        (await client.PutAsync($"{basePath}/reached-items/{seed.ItemIds[0]}", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK, "a reached item replays even after completion");
    }

    [Fact]
    public async Task StartReplay_ReturnsOriginalSessionAndRejectsDifferentItineraryForTheKey()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(7, UserRole.Traveler);

        using var first = await StartAsync(client, seed.ItineraryId, FirstKey);
        using var replay = await StartAsync(client, seed.ItineraryId, FirstKey);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        replay.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadAsync(replay)).RootElement.GetProperty("sessionId").GetInt64()
            .Should().Be((await ReadAsync(first)).RootElement.GetProperty("sessionId").GetInt64());
        await factory.WithDbContextAsync(async db =>
        {
            (await db.TripSessions.CountAsync()).Should().Be(1);
            (await db.Set<TripStateHistory>().CountAsync()).Should().Be(1);
            return true;
        });

        await ExpectErrorAsync(
            await StartAsync(client, seed.ItineraryId + 999, FirstKey),
            HttpStatusCode.Conflict,
            "navigation.idempotency_key_payload_mismatch");
    }

    [Fact]
    public async Task Start_WithoutIdempotencyKeyIsAValidationError()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(7, UserRole.Traveler);

        using var response = await client.PostAsync(
            $"/api/v1/itineraries/{seed.ItineraryId}/navigation-sessions",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task FrozenSnapshot_RemainsNavigableAfterSourceStatusAndPoiChange()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(7, UserRole.Traveler);
        using var start = await StartAsync(client, seed.ItineraryId, FirstKey);
        using var startedJson = await ReadAsync(start);
        var sessionId = startedJson.RootElement.GetProperty("sessionId").GetInt64();
        var frozenName = Item(startedJson, 0).GetProperty("poiName").GetString();
        var frozenLatitude = Item(startedJson, 0).GetProperty("latitude").GetDecimal();

        await factory.WithDbContextAsync(async db =>
        {
            var itinerary = await db.Itineraries
                .Include(candidate => candidate.Items)
                .ThenInclude(item => item.PointOfInterest)
                .SingleAsync(candidate => candidate.Id == seed.ItineraryId);
            db.Entry(itinerary).Property(candidate => candidate.Status).CurrentValue = Itinerary.CancelledStatus;
            var poi = itinerary.Items.Single(item => item.SequenceNo == 1).PointOfInterest!;
            db.Entry(poi).Property(candidate => candidate.Name).CurrentValue = "Changed after start";
            db.Entry(poi).Property(candidate => candidate.Latitude).CurrentValue = 10.123456m;
            await db.SaveChangesAsync();
            return true;
        });

        using var read = await client.GetAsync($"/api/v1/navigation-sessions/{sessionId}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        using var readJson = await ReadAsync(read);
        Item(readJson, 0).GetProperty("poiName").GetString().Should().Be(frozenName);
        Item(readJson, 0).GetProperty("latitude").GetDecimal().Should().Be(frozenLatitude);

        using var reached = await client.PutAsync(
            $"/api/v1/navigation-sessions/{sessionId}/reached-items/{seed.ItemIds[0]}",
            content: null);
        reached.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Start_RejectsInactiveCoordinateFreeAndOutOfWindowItinerariesWithoutCreatingSessions()
    {
        await using var factory = new TripMateApiFactory();
        var ids = await SeedRejectedStartsAsync(factory);
        using var client = factory.CreateAuthenticatedClient(7, UserRole.Traveler);

        await ExpectErrorAsync(
            await StartAsync(client, ids.InactiveItineraryId, FirstKey),
            HttpStatusCode.Conflict,
            "navigation.itinerary_not_active");
        await ExpectErrorAsync(
            await StartAsync(client, ids.NoWaypointItineraryId, SecondKey),
            HttpStatusCode.UnprocessableEntity,
            "navigation.no_navigable_items");
        await ExpectErrorAsync(
            await StartAsync(client, ids.TomorrowItineraryId, "33333333-3333-3333-3333-333333333333"),
            HttpStatusCode.Conflict,
            "navigation.outside_trip_window");
        await factory.WithDbContextAsync(async db =>
        {
            (await db.TripSessions.CountAsync()).Should().Be(0);
            (await db.Set<TripStateHistory>().CountAsync()).Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task Start_KnownForeignItineraryReturnsNavigationAccessDenied()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(8, UserRole.Traveler);

        await ExpectErrorAsync(
            await StartAsync(client, seed.ItineraryId, FirstKey),
            HttpStatusCode.Forbidden,
            "navigation.access_denied");
    }

    private static JsonElement Item(JsonDocument session, int index) =>
        session.RootElement.GetProperty("items")[index];

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static async Task ExpectErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode statusCode,
        string errorCode)
    {
        using (response)
        {
            response.StatusCode.Should().Be(statusCode);
            using var json = await ReadAsync(response);
            json.RootElement.GetProperty("errorCode").GetString().Should().Be(errorCode);
        }
    }

    private static async Task<HttpResponseMessage> StartAsync(
        HttpClient client,
        long itineraryId,
        string idempotencyKey)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/itineraries/{itineraryId}/navigation-sessions");
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<(long ItineraryId, long[] ItemIds)> SeedAsync(TripMateApiFactory factory)
    {
        return await factory.WithDbContextAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Users.Add(Traveler(7, now));
            var category = PoiCategory.Create("Attraction", null);
            db.PoiCategories.Add(category);
            await db.SaveChangesAsync();
            var pois = new[] { "Marble Mountains", "Dragon Bridge", "My Khe Beach" }
                .Select((name, index) => PointOfInterest.Create(
                    category,
                    name,
                    16.0m + (index * 0.01m),
                    108.2m + (index * 0.01m),
                    7,
                    now))
                .ToArray();
            db.PointsOfInterest.AddRange(pois);
            await db.SaveChangesAsync();
            var itinerary = Itinerary.CreateManual(7, "Navigation", Itinerary.ActiveStatus, now);
            for (var index = 0; index < pois.Length; index++)
            {
                itinerary.AddItem(ItineraryItem.CreateVisit(
                    index + 1,
                    pois[index].Id,
                    now.AddHours(index + 1),
                    now.AddHours(index + 2),
                    index == 0,
                    0m,
                    "Planned"));
            }

            db.Itineraries.Add(itinerary);
            await db.SaveChangesAsync();
            return (
                itinerary.Id,
                itinerary.Items.OrderBy(item => item.SequenceNo).Select(item => item.Id).ToArray());
        });
    }

    private static async Task<(long InactiveItineraryId, long NoWaypointItineraryId, long TomorrowItineraryId)>
        SeedRejectedStartsAsync(TripMateApiFactory factory) =>
        await factory.WithDbContextAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Users.Add(Traveler(7, now));
            var category = PoiCategory.Create("Rejected start", null);
            db.PoiCategories.Add(category);
            await db.SaveChangesAsync();
            var poi = PointOfInterest.Create(category, "Inactive POI", 16m, 108m, 7, now);
            db.PointsOfInterest.Add(poi);
            await db.SaveChangesAsync();
            var inactive = Itinerary.CreateManual(7, "Inactive", Itinerary.DraftStatus, now);
            inactive.AddItem(ItineraryItem.CreateVisit(
                1, poi.Id, now.AddHours(1), now.AddHours(2), true, 0m, "Required"));
            var noWaypoint = Itinerary.CreateManual(7, "No waypoint", Itinerary.ActiveStatus, now);
            noWaypoint.AddItem(ItineraryItem.CreateRest(1, now.AddHours(1), now.AddHours(2), "Rest"));
            var tomorrow = Itinerary.CreateManual(7, "Tomorrow", Itinerary.ActiveStatus, now);
            tomorrow.AddItem(ItineraryItem.CreateVisit(
                1, poi.Id, now.AddDays(1), now.AddDays(1).AddHours(1), true, 0m, "Required"));
            db.Itineraries.AddRange(inactive, noWaypoint, tomorrow);
            await db.SaveChangesAsync();
            return (inactive.Id, noWaypoint.Id, tomorrow.Id);
        });

    private static User Traveler(long id, DateTimeOffset now) => new()
    {
        Id = id,
        Email = $"navigation-endpoint-{id}@example.com",
        FullName = "Navigation Endpoint",
        Role = UserRole.Traveler,
        Status = AccountStatus.Active,
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
    };
}