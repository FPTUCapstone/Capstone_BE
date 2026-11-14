using FluentAssertions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.ActiveTrips.GetList;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Admin.ActiveTrips;

public sealed class GetActiveTripsQueryHandlerTests
{
    [Fact]
    public async Task Handle_NonAdministrator_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetActiveTripsQueryHandler(db, new CurrentUser(4, "Traveler"), new Clock());

        var result = await handler.Handle(new GetActiveTripsQuery(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ActiveTripErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Handle_NoActiveTrips_ReturnsSuccessfulEmptyPage()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetActiveTripsQueryHandler(db, new CurrentUser(1, "Administrator"), new Clock());

        var result = await handler.Handle(new GetActiveTripsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
        result.Value.TotalPages.Should().Be(0);
        result.Value.Summary.Should().Be(new ActiveTripSummaryDto(0, 0, 0));
    }

    [Fact]
    public async Task Handle_FlagsOnlyEmergencyStoppedTripsWhosePaidBookingHasNoRefundYet()
    {
        await using var db = TestDbContext.Create();
        SeedTourSession(db, 1, 3, Booking(db, 10, 3, BookingStatus.Interrupted, PaymentStatus.Paid), interrupted: true);
        SeedTourSession(db, 2, 4, Booking(db, 11, 4, BookingStatus.Interrupted, PaymentStatus.Paid), interrupted: true);
        SeedTourSession(db, 3, 5, Booking(db, 12, 5, BookingStatus.Interrupted, PaymentStatus.Unpaid), interrupted: true);
        SeedTourSession(db, 4, 6, Booking(db, 13, 6, BookingStatus.Confirmed, PaymentStatus.Paid), interrupted: false);
        db.Refunds.Add(Refund.CreatePending(11, 1, 1, 100_000m, null, RefundInitiator.Administrator, Now));
        await db.SaveChangesAsync();
        var handler = new GetActiveTripsQueryHandler(db, new CurrentUser(1, "Administrator"), new Clock());

        var result = await handler.Handle(new GetActiveTripsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(4);
        result.Value.Items.Where(item => item.RefundReviewRequired).Select(item => item.TripId)
            .Should().Equal("1");
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 26, 1, 0, 0, TimeSpan.Zero);

    private static long Booking(TestDbContext db, long id, long travelerId, BookingStatus status, PaymentStatus payment)
    {
        db.Bookings.Add(TripMate.Domain.Entities.Booking.Restore(id, travelerId, 5, 1, 100_000m, status, payment,
            TripMate.Domain.Entities.Booking.DefaultCancellationPolicySnapshot));
        return id;
    }

    private static void SeedTourSession(TestDbContext db, long id, long travelerId, long bookingId, bool interrupted)
    {
        var traveler = new User
        {
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            Email = $"traveler-{travelerId}@example.test",
            FullName = $"Traveler {travelerId}",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        typeof(User).GetProperty(nameof(User.Id))!.SetValue(traveler, travelerId);
        var itinerary = Itinerary.CreateBookedTour(travelerId, "Booked tour", bookingId, 1, Now.AddDays(-1));
        typeof(Itinerary).GetProperty(nameof(Itinerary.Id))!.SetValue(itinerary, id);
        var item = TripSessionItem.Snapshot(id, 1, 1, "Stop", 16m, 108m, Now.AddHours(-1), Now.AddHours(1), true);
        var session = TripSession.Start(id, id, travelerId, Guid.NewGuid(), Now.AddHours(-1), Now.AddHours(6), [item]);
        typeof(TripSession).GetProperty(nameof(TripSession.Id))!.SetValue(session, id);
        typeof(TripSession).GetProperty(nameof(TripSession.Itinerary))!.SetValue(session, itinerary);
        if (interrupted)
            session.InterruptForEmergency(Now.AddMinutes(-5), nameof(EmergencyTourReason.WeatherEmergency)).Should().BeTrue();
        db.Users.Add(traveler);
        db.Itineraries.Add(itinerary);
        db.TripSessions.Add(session);
    }

    private sealed record CurrentUser(long? UserId, string? Role) : ICurrentUserService;

    private sealed class Clock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }
}