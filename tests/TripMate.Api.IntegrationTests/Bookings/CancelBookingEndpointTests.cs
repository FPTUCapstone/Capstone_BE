using System.Net;
using System.Net.Http.Json;
using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.Controllers.V1.Requests;
using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Bookings.Cancel;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Bookings;

[Collection(nameof(TripMateApiFactory))]
public sealed class CancelBookingEndpointTests
{
    [Fact]
    public async Task Cancel_MissingOrMalformedIdempotencyKey_ReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        var missing = await client.PostAsJsonAsync("/api/v1/bookings/1/cancellation", new CancelBookingRequest(null, null));
        client.DefaultRequestHeaders.Add("Idempotency-Key", "not-a-guid");
        var malformed = await client.PostAsJsonAsync("/api/v1/bookings/1/cancellation", new CancelBookingRequest(null, null));

        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cancel_ComposedReasonOverBookingLimit_ReturnsBadRequestInsteadOfServerError()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        var response = await PostCancelAsync(client, 1, Guid.NewGuid(),
            new string('C', 64), new string('D', 300));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cancel_UnauthenticatedOrNonSupportedRole_IsRejectedBeforeMutation()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedPendingBookingAsync(factory);
        using var anonymousClient = factory.CreateClient();
        using var administratorClient = factory.CreateAuthenticatedClient(seed.TravelerUserId, UserRole.Administrator);

        var anonymous = await PostCancelAsync(anonymousClient, seed.BookingId, Guid.NewGuid());
        var administrator = await PostCancelAsync(administratorClient, seed.BookingId, Guid.NewGuid());

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        administrator.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertUnchangedAsync(factory, seed.BookingId, seed.ReservedCapacity);
    }

    [Fact]
    public async Task Cancel_TravelerOrOperatorWithoutOwnership_IsForbiddenAndDoesNotMutate()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedPendingBookingAsync(factory);
        using var unrelatedTraveler = factory.CreateAuthenticatedClient(seed.TravelerUserId + 1000, UserRole.Traveler);
        using var unrelatedOperator = factory.CreateAuthenticatedClient(seed.OperatorUserId + 1000, UserRole.TourOperator);

        var travelerResponse = await PostCancelAsync(unrelatedTraveler, seed.BookingId, Guid.NewGuid());
        var operatorResponse = await PostCancelAsync(unrelatedOperator, seed.BookingId, Guid.NewGuid(), "Schedule", "Cancelled by operator");

        travelerResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        operatorResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertUnchangedAsync(factory, seed.BookingId, seed.ReservedCapacity);
    }

    [Fact]
    public async Task Cancel_PendingPayment_DoesNotCreateRefund_AndReplayDoesNotDuplicateOperation()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedPendingBookingAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.TravelerUserId, UserRole.Traveler);
        var key = Guid.NewGuid();

        var first = await PostCancelAsync(client, seed.BookingId, key);
        var replay = await PostCancelAsync(client, seed.BookingId, key);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.Content.ReadFromJsonAsync<CancelBookingResponse>())
            .Should().BeEquivalentTo(await replay.Content.ReadFromJsonAsync<CancelBookingResponse>());
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Refunds.CountAsync()).Should().Be(0);
            (await db.BookingCancellationOperations.CountAsync()).Should().Be(1);
            (await db.Bookings.SingleAsync()).Status.Should().Be(BookingStatus.Cancelled);
            (await db.Tickets.SingleAsync()).Status.Should().Be(TicketStatus.Cancelled);
            (await db.TourSchedules.SingleAsync()).ReservedCapacity.Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task Cancel_ReusedIdempotencyKeyWithDifferentPayload_ReturnsConflictWithoutSecondMutation()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedPendingBookingAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.TravelerUserId, UserRole.Traveler);
        var key = Guid.NewGuid();

        var first = await PostCancelAsync(client, seed.BookingId, key);
        var mismatch = await PostCancelAsync(client, seed.BookingId, key, null, "Different payload");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        mismatch.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.BookingCancellationOperations.CountAsync()).Should().Be(1);
            (await db.Refunds.CountAsync()).Should().Be(0);
            (await db.Tickets.SingleAsync()).Status.Should().Be(TicketStatus.Cancelled);
            (await db.TourSchedules.SingleAsync()).ReservedCapacity.Should().Be(0);
            return true;
        });
    }

    private static Task<HttpResponseMessage> PostCancelAsync(HttpClient client, long bookingId, Guid key,
        string? category = null, string? detail = null)
    {
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", key.ToString());
        return client.PostAsJsonAsync($"/api/v1/bookings/{bookingId}/cancellation", new CancelBookingRequest(category, detail));
    }

    private static async Task AssertUnchangedAsync(TripMateApiFactory factory, long bookingId, int reservedCapacity) =>
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Bookings.SingleAsync(booking => booking.Id == bookingId)).Status.Should().Be(BookingStatus.PendingPayment);
            (await db.Refunds.CountAsync()).Should().Be(0);
            (await db.BookingCancellationOperations.CountAsync()).Should().Be(0);
            (await db.Tickets.SingleAsync()).Status.Should().Be(TicketStatus.Valid);
            (await db.TourSchedules.SingleAsync()).ReservedCapacity.Should().Be(reservedCapacity);
            return true;
        });

    private static async Task<BookingSeed> SeedPendingBookingAsync(TripMateApiFactory factory) =>
        await factory.WithDbContextAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var traveler = await AddUserAsync(db, UserRole.Traveler, now);
            var tourOperator = await AddUserAsync(db, UserRole.TourOperator, now);
            var unrelatedTraveler = await AddUserAsync(db, UserRole.Traveler, now);
            var unrelatedOperator = await AddUserAsync(db, UserRole.TourOperator, now);
            var tour = CreateTour(tourOperator.Id);
            db.Tours.Add(tour);
            await db.SaveChangesAsync();
            var schedule = CreateSchedule(tour, now.AddDays(10), 1);
            db.TourSchedules.Add(schedule);
            await db.SaveChangesAsync();
            var booking = Booking.Create(traveler.Id, schedule.Id, 1, 500_000m,
                BookingStatus.PendingPayment, PaymentStatus.Unpaid);
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();
            db.Tickets.Add(CreateTicket(booking.Id));
            await db.SaveChangesAsync();

            return new BookingSeed(booking.Id, traveler.Id, tourOperator.Id, schedule.ReservedCapacity,
                unrelatedTraveler.Id, unrelatedOperator.Id);
        });

    private static async Task<User> AddUserAsync(TestApiDbContext db, UserRole role, DateTimeOffset now)
    {
        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@example.com",
            FullName = role.ToString(),
            Role = role,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static Tour CreateTour(long operatorUserId)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), operatorUserId);
        Set(tour, nameof(Tour.Title), "Cancellation endpoint tour");
        Set(tour, nameof(Tour.BasePrice), 500_000m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Approved);
        return tour;
    }

    private static TourSchedule CreateSchedule(Tour tour, DateTimeOffset departure, int reservedCapacity)
    {
        var schedule = (TourSchedule)Activator.CreateInstance(typeof(TourSchedule), nonPublic: true)!;
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        Set(schedule, nameof(TourSchedule.StartAtUtc), departure);
        Set(schedule, nameof(TourSchedule.EndAtUtc), departure.AddHours(8));
        Set(schedule, nameof(TourSchedule.TotalCapacity), 10);
        Set(schedule, nameof(TourSchedule.ReservedCapacity), reservedCapacity);
        Set(schedule, nameof(TourSchedule.Status), TourScheduleStatus.Scheduled);
        return schedule;
    }

    private static Ticket CreateTicket(long bookingId)
    {
        var ticket = (Ticket)Activator.CreateInstance(typeof(Ticket), nonPublic: true)!;
        Set(ticket, nameof(Ticket.BookingId), bookingId);
        Set(ticket, nameof(Ticket.Status), TicketStatus.Valid);
        return ticket;
    }

    private static void Set<T>(object instance, string property, T value) => instance.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .SetValue(instance, value);

    private sealed record BookingSeed(long BookingId, long TravelerUserId, long OperatorUserId,
        int ReservedCapacity, long UnrelatedTravelerUserId, long UnrelatedOperatorUserId);
}