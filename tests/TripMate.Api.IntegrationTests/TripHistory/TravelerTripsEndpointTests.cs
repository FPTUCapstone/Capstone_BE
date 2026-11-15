using System.Net;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.TripHistory;

[Collection(nameof(TripMateApiFactory))]
public sealed class TravelerTripsEndpointTests
{
    private const string ListRoute = "/api/v1/traveler/trips";
    private const long Alice = 11;
    private const long Bob = 12;

    private static readonly DateTimeOffset Departure = new(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);

    private sealed record Seeded(
        long CompletedBookingId,
        long UpcomingBookingId,
        long CancelledBookingId,
        long BobBookingId,
        long CompletedItineraryId,
        long UpcomingItineraryId,
        long ExpiredItineraryId,
        long BobItineraryId);

    private static async Task<Seeded> SeedAsync(TestApiDbContext db)
    {
        var seed = new TripHistoryApiSeed(db);
        await seed.AddUserAsync(Alice);
        await seed.AddUserAsync(Bob);

        var completed = await seed.AddBookingAsync(Alice, await seed.AddScheduleAsync(Departure), BookingStatus.Completed);
        await seed.AddTicketAsync(completed.Id, TicketStatus.Used, id: 3);
        await seed.AddTicketAsync(completed.Id, TicketStatus.Valid, id: 9);
        var upcoming = await seed.AddBookingAsync(Alice, await seed.AddScheduleAsync(Departure.AddDays(200)), BookingStatus.Confirmed);
        var cancelled = await seed.AddBookingAsync(Alice, await seed.AddScheduleAsync(Departure.AddDays(-30)), BookingStatus.Cancelled);
        await seed.MarkCancelledAsync(cancelled, Departure.AddDays(-40), "Change of plans");
        await seed.AddPaymentAndRefundAsync(cancelled.Id, id: 50, PaymentGateway.PayOS, 800_000m);
        var bobBooking = await seed.AddBookingAsync(Bob, await seed.AddScheduleAsync(Departure), BookingStatus.Completed);

        var done = await seed.AddItineraryAsync(Alice, visitCount: 3);
        await seed.AddSessionAsync(done, Alice, Departure.AddDays(5), Departure.AddDays(5).AddHours(6), reachedItems: 2, TimeSpan.FromDays(30));
        var planned = await seed.AddItineraryAsync(Alice, validFromUtc: Departure.AddDays(300));
        var expired = await seed.AddItineraryAsync(Alice, visitCount: 2);
        // Started long ago and past its expiry; the database still says it is open (lazy expiry).
        await seed.AddSessionAsync(expired, Alice, Departure.AddDays(-60), endedAtUtc: null, reachedItems: 1, TimeSpan.FromHours(2));
        var bobItinerary = await seed.AddItineraryAsync(Bob);
        await seed.AddSessionAsync(bobItinerary, Bob, Departure, Departure.AddHours(3), reachedItems: 1, TimeSpan.FromDays(30));

        return new Seeded(completed.Id, upcoming.Id, cancelled.Id, bobBooking.Id, done.Id, planned.Id, expired.Id, bobItinerary.Id);
    }

    private static async Task<(TripMateApiFactory Factory, Seeded Seed)> NewFactoryAsync()
    {
        var factory = new TripMateApiFactory();
        var seed = await factory.WithDbContextAsync(SeedAsync);
        return (factory, seed);
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, "the response body was {0}", payload);
        return JsonDocument.Parse(payload);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string errorCode)
    {
        using var body = await ReadAsync(response, status);
        body.RootElement.GetProperty("status").GetInt32().Should().Be((int)status);
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(errorCode);
    }

    // --- authentication and authorization ---

    [Theory]
    [InlineData(ListRoute + "?tab=Completed")]
    [InlineData(ListRoute + "/commerceBooking/1")]
    public async Task Anonymous_IsUnauthorizedWithTheFeatureCode(string route)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(route);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "trip_history.unauthorized");
    }

    [Theory]
    [InlineData(UserRole.TourOperator)]
    [InlineData(UserRole.Administrator)]
    public async Task NonTraveler_IsForbiddenWithTheFeatureCode(UserRole role)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, role);

        using var response = await client.GetAsync(ListRoute + "?tab=Completed");

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "trip_history.forbidden");
    }

    [Fact]
    public async Task InactiveTraveler_IsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        await factory.WithDbContextAsync(async db =>
        {
            await new TripHistoryApiSeed(db).AddUserAsync(21, UserRole.Traveler, AccountStatus.Locked);
            return 0;
        });
        using var client = factory.CreateAuthenticatedClient(21, UserRole.Traveler);

        using var response = await client.GetAsync(ListRoute + "?tab=Completed");

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "trip_history.forbidden");
    }

    // --- validation codes ---

    [Theory]
    [InlineData("", "trip_history.invalid_tab")]
    [InlineData("?tab=Foo", "trip_history.invalid_tab")]
    [InlineData("?tab=completed", "trip_history.invalid_tab")]
    [InlineData("?tab=Completed&tripType=Foo", "trip_history.invalid_trip_type")]
    [InlineData("?tab=Completed&fromDate=21/10/2026", "trip_history.invalid_date_range")]
    [InlineData("?tab=Completed&fromDate=2026-10-22&toDate=2026-10-21", "trip_history.invalid_date_range")]
    [InlineData("?tab=Completed&page=0", "trip_history.invalid_paging")]
    [InlineData("?tab=Completed&pageSize=0", "trip_history.invalid_paging")]
    [InlineData("?tab=Completed&pageSize=51", "trip_history.invalid_paging")]
    [InlineData("?tab=Completed&page=21&pageSize=50", "trip_history.invalid_paging")]
    public async Task InvalidList_ReturnsBadRequestWithTheStableCode(string query, string errorCode)
    {
        var (factory, _) = await NewFactoryAsync();
        await using var _f = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);

        using var response = await client.GetAsync(ListRoute + query);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, errorCode);
    }

    [Fact]
    public async Task List_AtTheWindowCap_IsAccepted()
    {
        var (factory, _) = await NewFactoryAsync();
        await using var _f = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);

        using var response = await client.GetAsync(ListRoute + "?tab=Completed&page=20&pageSize=50");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/serviceBooking/1")]
    [InlineData("/Itinerary/1")]
    [InlineData("/commerceBooking/0")]
    [InlineData("/commerceBooking/-4")]
    [InlineData("/commerceBooking/abc")]
    [InlineData("/commerceBooking/99999999999999999999")]
    public async Task InvalidDetailRecord_ReturnsBadRequestWithTheStableCode(string path)
    {
        var (factory, _) = await NewFactoryAsync();
        await using var _f = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);

        using var response = await client.GetAsync(ListRoute + path);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "trip_history.invalid_record");
    }

    // --- not found and ownership ---

    [Fact]
    public async Task Detail_UnknownAndForeignRecords_AreIndistinguishable()
    {
        var (factory, seed) = await NewFactoryAsync();
        await using var _f = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);

        using var missing = await client.GetAsync($"{ListRoute}/commerceBooking/987654");
        using var foreignBooking = await client.GetAsync($"{ListRoute}/commerceBooking/{seed.BobBookingId}");
        using var foreignItinerary = await client.GetAsync($"{ListRoute}/itinerary/{seed.BobItineraryId}");

        await AssertProblemAsync(missing, HttpStatusCode.NotFound, "trip_history.record_not_found");
        await AssertProblemAsync(foreignBooking, HttpStatusCode.NotFound, "trip_history.record_not_found");
        await AssertProblemAsync(foreignItinerary, HttpStatusCode.NotFound, "trip_history.record_not_found");
    }

    [Fact]
    public async Task List_EachTravelerSeesOnlyTheirOwnRecords()
    {
        var (factory, seed) = await NewFactoryAsync();
        await using var _f = factory;
        using var alice = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);
        using var bob = factory.CreateAuthenticatedClient(Bob, UserRole.Traveler);

        using var aliceResponse = await alice.GetAsync(ListRoute + "?tab=Completed");
        using var bobResponse = await bob.GetAsync(ListRoute + "?tab=Completed");

        using var aliceBody = await ReadAsync(aliceResponse, HttpStatusCode.OK);
        using var bobBody = await ReadAsync(bobResponse, HttpStatusCode.OK);
        var aliceIds = aliceBody.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => (item.GetProperty("kind").GetString(), item.GetProperty("id").GetInt64())).ToArray();
        var bobIds = bobBody.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => (item.GetProperty("kind").GetString(), item.GetProperty("id").GetInt64())).ToArray();

        aliceIds.Should().BeEquivalentTo(new[]
        {
            ("commerceBooking", seed.CompletedBookingId),
            ("itinerary", seed.CompletedItineraryId),
            ("itinerary", seed.ExpiredItineraryId),
        });
        bobIds.Should().BeEquivalentTo(new[]
        {
            ("commerceBooking", seed.BobBookingId),
            ("itinerary", seed.BobItineraryId),
        });
    }

    // --- list contract ---

    [Fact]
    public async Task List_Completed_ReturnsTheContractShapeAndSummary()
    {
        var (factory, seed) = await NewFactoryAsync();
        await using var _f = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);

        using var response = await client.GetAsync(ListRoute + "?tab=Completed");

        using var body = await ReadAsync(response, HttpStatusCode.OK);
        var root = body.RootElement;
        root.GetProperty("page").GetInt32().Should().Be(1);
        root.GetProperty("pageSize").GetInt32().Should().Be(20);
        root.GetProperty("totalCount").GetInt32().Should().Be(3);
        // 3 completed trips; Reached items: 2 (finished itinerary) + 1 (expired-open itinerary).
        root.GetProperty("summary").GetProperty("totalCompletedTrips").GetInt32().Should().Be(3);
        root.GetProperty("summary").GetProperty("totalVisitedPois").GetInt32().Should().Be(3);

        var items = root.GetProperty("items").EnumerateArray().ToArray();
        // Departure descending: the 2026-10-06 itinerary, the 2026-10-01 booking, then the expired one (2026-08-02).
        items.Select(item => item.GetProperty("id").GetInt64())
            .Should().Equal(seed.CompletedItineraryId, seed.CompletedBookingId, seed.ExpiredItineraryId);

        var booking = items[1];
        booking.GetProperty("kind").GetString().Should().Be("commerceBooking");
        booking.GetProperty("tripType").GetString().Should().Be("TourBooking");
        booking.GetProperty("tab").GetString().Should().Be("Completed");
        booking.GetProperty("title").GetString().Should().Be("Hoi An Lantern Tour");
        booking.GetProperty("status").GetString().Should().Be("Completed");
        booking.GetProperty("bookingCode").GetString().Should().Be($"TM-{seed.CompletedBookingId:D6}");
        booking.GetProperty("participantCount").GetInt32().Should().Be(2);
        booking.GetProperty("totalAmount").GetDecimal().Should().Be(3_200_000m);
        booking.GetProperty("reviewableRecord").GetProperty("kind").GetString().Should().Be("commerceBooking");
        booking.GetProperty("reviewableRecord").GetProperty("id").GetInt64().Should().Be(seed.CompletedBookingId);
        var actions = booking.GetProperty("actions");
        actions.GetProperty("canReview").GetBoolean().Should().BeTrue();
        actions.GetProperty("canViewETicket").GetBoolean().Should().BeTrue();
        actions.GetProperty("ticketId").GetInt64().Should().Be(9);
        actions.GetProperty("canViewRefund").GetBoolean().Should().BeFalse();
        booking.GetProperty("review").GetProperty("exists").GetBoolean().Should().BeFalse();
        booking.GetProperty("review").TryGetProperty("reviewId", out _).Should().BeFalse();
        booking.TryGetProperty("stopCount", out _).Should().BeFalse();
        booking.TryGetProperty("visitedCount", out _).Should().BeFalse();
        DateTimeOffset.Parse(booking.GetProperty("departureAtUtc").GetString()!).Should().Be(Departure);

        var itinerary = items[0];
        itinerary.GetProperty("kind").GetString().Should().Be("itinerary");
        itinerary.GetProperty("tripType").GetString().Should().Be("SelfPlannedItinerary");
        itinerary.GetProperty("status").GetString().Should().Be("Completed");
        itinerary.GetProperty("stopCount").GetInt32().Should().Be(3);
        itinerary.GetProperty("visitedCount").GetInt32().Should().Be(2);
        itinerary.GetProperty("reviewableRecord").ValueKind.Should().Be(JsonValueKind.Null);
        itinerary.GetProperty("review").ValueKind.Should().Be(JsonValueKind.Null);
        itinerary.GetProperty("actions").GetProperty("canReview").GetBoolean().Should().BeFalse();
        itinerary.GetProperty("actions").GetProperty("ticketId").ValueKind.Should().Be(JsonValueKind.Null);
        itinerary.TryGetProperty("bookingCode", out _).Should().BeFalse();
        itinerary.TryGetProperty("totalAmount", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("Upcoming")]
    [InlineData("Cancelled")]
    public async Task List_OtherTabs_OmitTheSummary(string tab)
    {
        var (factory, _) = await NewFactoryAsync();
        await using var _f = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);

        using var response = await client.GetAsync($"{ListRoute}?tab={tab}");

        using var body = await ReadAsync(response, HttpStatusCode.OK);
        body.RootElement.TryGetProperty("summary", out _).Should().BeFalse();
    }

    [Fact]
    public async Task List_Cancelled_ExposesTheRefundActionAndUpcomingTheRest()
    {
        var (factory, seed) = await NewFactoryAsync();
        await using var _f = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);

        using var cancelled = await client.GetAsync(ListRoute + "?tab=Cancelled");
        using var upcoming = await client.GetAsync(ListRoute + "?tab=Upcoming");

        using var cancelledBody = await ReadAsync(cancelled, HttpStatusCode.OK);
        var only = cancelledBody.RootElement.GetProperty("items").EnumerateArray().Single();
        only.GetProperty("id").GetInt64().Should().Be(seed.CancelledBookingId);
        only.GetProperty("actions").GetProperty("canViewRefund").GetBoolean().Should().BeTrue();
        only.GetProperty("actions").GetProperty("canReview").GetBoolean().Should().BeFalse();

        using var upcomingBody = await ReadAsync(upcoming, HttpStatusCode.OK);
        upcomingBody.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64())
            .Should().Equal(seed.UpcomingItineraryId, seed.UpcomingBookingId);
    }

    [Fact]
    public async Task List_TripTypeAndDateFilters_ApplyThroughHttp()
    {
        var (factory, seed) = await NewFactoryAsync();
        await using var _f = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);

        // 2026-10-01T02:00Z is 2026-10-01 09:00 in Vietnam.
        using var bookingsOnly = await client.GetAsync(ListRoute + "?tab=Completed&tripType=TourBooking");
        using var oneDay = await client.GetAsync(ListRoute + "?tab=Completed&fromDate=2026-10-01&toDate=2026-10-01");
        using var otherDay = await client.GetAsync(ListRoute + "?tab=Completed&fromDate=2026-09-30&toDate=2026-09-30");

        using var bookingsBody = await ReadAsync(bookingsOnly, HttpStatusCode.OK);
        bookingsBody.RootElement.GetProperty("items").EnumerateArray().Single()
            .GetProperty("id").GetInt64().Should().Be(seed.CompletedBookingId);
        bookingsBody.RootElement.GetProperty("summary").GetProperty("totalVisitedPois").GetInt32().Should().Be(0);
        using var dayBody = await ReadAsync(oneDay, HttpStatusCode.OK);
        dayBody.RootElement.GetProperty("items").EnumerateArray().Single()
            .GetProperty("id").GetInt64().Should().Be(seed.CompletedBookingId);
        using var otherBody = await ReadAsync(otherDay, HttpStatusCode.OK);
        otherBody.RootElement.GetProperty("totalCount").GetInt32().Should().Be(0);
        otherBody.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task List_EmptyTab_Returns200WithNoItems()
    {
        await using var factory = new TripMateApiFactory();
        await factory.WithDbContextAsync(async db =>
        {
            await new TripHistoryApiSeed(db).AddUserAsync(31);
            return 0;
        });
        using var client = factory.CreateAuthenticatedClient(31, UserRole.Traveler);

        using var response = await client.GetAsync(ListRoute + "?tab=Completed");

        using var body = await ReadAsync(response, HttpStatusCode.OK);
        body.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
        body.RootElement.GetProperty("totalCount").GetInt32().Should().Be(0);
        body.RootElement.GetProperty("summary").GetProperty("totalCompletedTrips").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task List_PagesNeverRepeatOrSkipOverMixedRecords()
    {
        await using var factory = new TripMateApiFactory();
        await factory.WithDbContextAsync(async db =>
        {
            var seed = new TripHistoryApiSeed(db);
            await seed.AddUserAsync(41);
            for (var index = 0; index < 13; index++)
            {
                // Pairs of equal departures across both kinds are the unstable-paging worst case.
                var departure = Departure.AddDays(index / 2);
                await seed.AddBookingAsync(41, await seed.AddScheduleAsync(departure), BookingStatus.Confirmed);
                if (index < 12)
                {
                    await seed.AddItineraryAsync(41, validFromUtc: departure);
                }
            }

            return 0;
        });
        using var client = factory.CreateAuthenticatedClient(41, UserRole.Traveler);

        using var all = await client.GetAsync(ListRoute + "?tab=Upcoming&pageSize=50");
        using var allBody = await ReadAsync(all, HttpStatusCode.OK);
        var everything = allBody.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => $"{item.GetProperty("kind").GetString()}:{item.GetProperty("id").GetInt64()}").ToArray();
        var paged = new List<string>();
        for (var page = 1; page <= 4; page++)
        {
            using var response = await client.GetAsync($"{ListRoute}?tab=Upcoming&page={page}&pageSize=7");
            using var body = await ReadAsync(response, HttpStatusCode.OK);
            body.RootElement.GetProperty("totalCount").GetInt32().Should().Be(25);
            paged.AddRange(body.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => $"{item.GetProperty("kind").GetString()}:{item.GetProperty("id").GetInt64()}"));
        }

        everything.Should().HaveCount(25);
        paged.Should().Equal(everything);
        paged.Should().OnlyHaveUniqueItems();
    }

    // --- detail contract ---

    [Fact]
    public async Task Detail_Booking_ReturnsTheCardAndTheTourDetail()
    {
        var (factory, seed) = await NewFactoryAsync();
        await using var _f = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);

        using var response = await client.GetAsync($"{ListRoute}/commerceBooking/{seed.CompletedBookingId}");

        using var body = await ReadAsync(response, HttpStatusCode.OK);
        var root = body.RootElement;
        root.GetProperty("kind").GetString().Should().Be("commerceBooking");
        root.GetProperty("actions").GetProperty("ticketId").GetInt64().Should().Be(9);
        root.GetProperty("tour").GetProperty("title").GetString().Should().Be("Hoi An Lantern Tour");
        root.GetProperty("tour").GetProperty("destinationSummary").GetString().Should().Be("Hoi An");
        root.GetProperty("schedule").GetProperty("meetingPoint").GetString().Should().Be("Old Town gate");
        root.GetProperty("participants").GetProperty("adult").GetInt32().Should().Be(0);
        root.TryGetProperty("cancellation", out _).Should().BeFalse();
        root.TryGetProperty("refund", out _).Should().BeFalse();
        root.TryGetProperty("stops", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Detail_CancelledBooking_ReturnsTheCancellationAndRefund()
    {
        var (factory, seed) = await NewFactoryAsync();
        await using var _f = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);

        using var response = await client.GetAsync($"{ListRoute}/commerceBooking/{seed.CancelledBookingId}");

        using var body = await ReadAsync(response, HttpStatusCode.OK);
        var root = body.RootElement;
        root.GetProperty("cancellation").GetProperty("reason").GetString().Should().Be("Change of plans");
        root.GetProperty("refund").GetProperty("status").GetString().Should().Be("Processed");
        root.GetProperty("refund").GetProperty("amount").GetDecimal().Should().Be(800_000m);
        root.GetProperty("refund").GetProperty("channel").GetString().Should().Be("PayOS");
    }

    [Fact]
    public async Task Detail_CompletedItinerary_ReturnsStopsWithVisitDataAndTheSession()
    {
        var (factory, seed) = await NewFactoryAsync();
        await using var _f = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);

        using var response = await client.GetAsync($"{ListRoute}/itinerary/{seed.CompletedItineraryId}");

        using var body = await ReadAsync(response, HttpStatusCode.OK);
        var root = body.RootElement;
        root.GetProperty("tab").GetString().Should().Be("Completed");
        root.GetProperty("session").GetProperty("completionReason").GetString().Should().Be("TravelerStopped");
        var stops = root.GetProperty("stops").EnumerateArray().ToArray();
        stops.Select(stop => stop.GetProperty("visitStatus").GetString()).Should().Equal("Reached", "Reached", "Pending");
        stops[0].GetProperty("poiName").GetString().Should().Be("Dragon Bridge");
        stops[0].TryGetProperty("reachedAtUtc", out _).Should().BeTrue();
        stops[2].TryGetProperty("reachedAtUtc", out _).Should().BeFalse();
        root.TryGetProperty("tour", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Detail_ItineraryWhoseSessionExpired_IsCompletedByExpiry()
    {
        var (factory, seed) = await NewFactoryAsync();
        await using var _f = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);

        using var response = await client.GetAsync($"{ListRoute}/itinerary/{seed.ExpiredItineraryId}");

        using var body = await ReadAsync(response, HttpStatusCode.OK);
        body.RootElement.GetProperty("tab").GetString().Should().Be("Completed");
        body.RootElement.GetProperty("session").GetProperty("completionReason").GetString().Should().Be("Expired");
        body.RootElement.GetProperty("visitedCount").GetInt32().Should().Be(1);
    }

    // --- read-only (PC-03) ---

    [Fact]
    public async Task Reading_ChangesNothingInTheDatabase()
    {
        var (factory, seed) = await NewFactoryAsync();
        await using var _f = factory;
        using var client = factory.CreateAuthenticatedClient(Alice, UserRole.Traveler);
        var before = await Snapshot(factory);

        foreach (var tab in new[] { "Upcoming", "Completed", "Cancelled" })
        {
            (await client.GetAsync($"{ListRoute}?tab={tab}")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await client.GetAsync($"{ListRoute}/commerceBooking/{seed.CompletedBookingId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"{ListRoute}/itinerary/{seed.ExpiredItineraryId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await Snapshot(factory);
        after.Should().Equal(before);
        // The lazily expired session stays "open" in storage: reading never closes it.
        after.Should().Contain(line =>
            line.StartsWith($"session:{seed.ExpiredItineraryId}:") && !line.Contains(":Completed:"));
    }

    private static Task<string[]> Snapshot(TripMateApiFactory factory) =>
        factory.WithDbContextAsync(async db =>
        {
            var bookings = (await db.Bookings.AsNoTracking().OrderBy(b => b.Id).ToListAsync())
                .Select(b => $"booking:{b.Id}:{b.Status}:{b.CancelledAtUtc:O}:{b.CancelReason}");
            var itineraries = (await db.Itineraries.AsNoTracking().OrderBy(i => i.Id).ToListAsync())
                .Select(i => $"itinerary:{i.Id}:{i.Status}:{i.Version}:{i.UpdatedAtUtc:O}");
            var sessions = (await db.TripSessions.AsNoTracking().OrderBy(s => s.Id).ToListAsync())
                .Select(s => $"session:{s.ItineraryId}:{s.FsmState}:{s.EndedAtUtc:O}:{s.CompletionReason}");
            var reviews = await db.TripReviews.AsNoTracking().CountAsync();
            var refunds = (await db.Refunds.AsNoTracking().OrderBy(r => r.Id).ToListAsync())
                .Select(r => $"refund:{r.Id}:{r.Status}");
            return bookings.Concat(itineraries).Concat(sessions).Concat(refunds).Append($"reviews:{reviews}").ToArray();
        });
}