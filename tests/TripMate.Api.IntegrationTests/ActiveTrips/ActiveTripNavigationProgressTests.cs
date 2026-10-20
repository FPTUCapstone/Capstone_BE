using System.Net;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.ActiveTrips;

/// <summary>
/// UC-13 → UC-58/UC-59 end to end: the progress a Traveler records through the navigation API is
/// exactly what Operations Staff sees for that session, the shared planning status is never
/// touched, and the session leaves both screens once it closes.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class ActiveTripNavigationProgressTests
{
    [Fact]
    public async Task StaffSeesTheSessionsOwnProgress_InMemory()
    {
        await using var factory = new TripMateApiFactory();

        await AssertStaffSeesSessionProgressAsync(
            factory,
            operation => factory.WithDbContextAsync(async db =>
            {
                await operation(db);
                return true;
            }));
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task StaffSeesTheSessionsOwnProgress_SqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);

        await AssertStaffSeesSessionProgressAsync(
            factory,
            async operation =>
            {
                await using var db = database.CreateDbContext();
                await operation(db);
            });
    }

    private static async Task AssertStaffSeesSessionProgressAsync(
        TripMateApiFactory factory,
        Func<Func<IApplicationDbContext, Task>, Task> withDb)
    {
        Seed seed = null!;
        await withDb(async db => seed = await SeedAsync(db));
        using var traveler = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);
        using var staff = factory.CreateAuthenticatedClient(seed.StaffId, UserRole.Staff);

        using var startRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/itineraries/{seed.ItineraryId}/navigation-sessions");
        startRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var started = await traveler.SendAsync(startRequest);
        started.StatusCode.Should().Be(HttpStatusCode.Created);
        var sessionId = (await ReadAsync(started)).RootElement.GetProperty("sessionId").GetInt64();
        var sessionPath = $"/api/v1/navigation-sessions/{sessionId}";
        var detailPath = $"/api/v1/admin/trips/active/{sessionId}";

        using (var reached = await traveler.PutAsync($"{sessionPath}/reached-items/{seed.ItemIds[2]}", content: null))
        {
            reached.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var skipped = await traveler.PutAsync($"{sessionPath}/skipped-items/{seed.ItemIds[1]}", content: null))
        {
            skipped.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var list = await staff.GetAsync("/api/v1/admin/trips/active"))
        {
            list.StatusCode.Should().Be(HttpStatusCode.OK);
            (await ReadAsync(list)).RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("tripId").GetString())
                .Should().Contain(sessionId.ToString());
        }

        using (var detail = await staff.GetAsync(detailPath))
        {
            detail.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = await ReadAsync(detail);
            json.RootElement.GetProperty("currentState").GetString().Should().Be(TripSession.ExploringState);
            var progress = json.RootElement.GetProperty("itineraryProgress").EnumerateArray().ToArray();
            progress.Select(item => item.GetProperty("itemId").GetString())
                .Should().Equal(seed.ItemIds.Select(id => id.ToString()));
            progress.Select(item => item.GetProperty("status").GetString()).Should().Equal(
                TripSessionItem.PendingStatus,
                TripSessionItem.SkippedStatus,
                TripSessionItem.ReachedStatus);
        }

        await withDb(async db =>
            (await db.ItineraryItems.AsNoTracking()
                    .Where(item => item.ItineraryId == seed.ItineraryId)
                    .Select(item => item.Status)
                    .ToListAsync())
                .Should().OnlyContain(
                    status => status == ItineraryItem.StatusPlanned,
                    "navigation never writes planning status"));

        using (var stopped = await traveler.PutAsync($"{sessionPath}/completion", content: null))
        {
            stopped.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var afterStop = await staff.GetAsync(detailPath))
        {
            afterStop.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        using (var listAfterStop = await staff.GetAsync("/api/v1/admin/trips/active"))
        {
            (await ReadAsync(listAfterStop)).RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("tripId").GetString())
                .Should().NotContain(sessionId.ToString());
        }
    }

    private static async Task<Seed> SeedAsync(IApplicationDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");
        var traveler = User($"uc13-uc59-traveler-{suffix}@example.com", UserRole.Traveler, now);
        var staff = User($"uc13-uc59-staff-{suffix}@example.com", UserRole.Staff, now);
        db.Users.AddRange(traveler, staff);
        var category = PoiCategory.Create($"UC-59 progress {suffix}", null);
        db.PoiCategories.Add(category);
        await db.SaveChangesAsync(CancellationToken.None);

        var pois = new[] { "Marble Mountains", "Dragon Bridge", "My Khe Beach" }
            .Select((name, index) => PointOfInterest.Create(
                category,
                name,
                16.0m + (index * 0.01m),
                108.2m + (index * 0.01m),
                traveler.Id,
                now))
            .ToArray();
        db.PointsOfInterest.AddRange(pois);
        await db.SaveChangesAsync(CancellationToken.None);

        var itinerary = Itinerary.CreateManual(traveler.Id, "UC-59 progress", Itinerary.ActiveStatus, now);
        for (var index = 0; index < pois.Length; index++)
        {
            itinerary.AddItem(ItineraryItem.CreateVisit(
                index + 1,
                pois[index].Id,
                now.AddHours(index + 1),
                now.AddHours(index + 2),
                index == 0,
                0m,
                "Planned stop"));
        }

        db.Itineraries.Add(itinerary);
        await db.SaveChangesAsync(CancellationToken.None);
        return new Seed(
            traveler.Id,
            staff.Id,
            itinerary.Id,
            itinerary.Items.OrderBy(item => item.SequenceNo).Select(item => item.Id).ToArray());
    }

    private static User User(string email, UserRole role, DateTimeOffset now) => new()
    {
        Email = email,
        FullName = role == UserRole.Staff ? "UC-59 Staff" : "UC-59 Traveler",
        Role = role,
        Status = AccountStatus.Active,
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
    };

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private sealed record Seed(long TravelerId, long StaffId, long ItineraryId, long[] ItemIds);
}