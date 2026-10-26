using FluentAssertions;

using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Application.Features.Traveler.Bookings.Create;
using TripMate.Application.Features.Traveler.Bookings.Expiry;
using TripMate.Application.Features.Traveler.Bookings.Quote;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Traveler.Bookings;

public sealed class VoucherSlotEligibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
    private static readonly TourBookingOptions Options = new()
    {
        PaymentExpiry = TimeSpan.FromMinutes(15),
        MaxUnpaidBookingsPerTraveler = 3,
    };

    [Theory]
    [InlineData(PriorBookingState.ExpiredUnpaid, true)]
    [InlineData(PriorBookingState.CancelledUnpaid, true)]
    [InlineData(PriorBookingState.PendingUnpaid, false)]
    [InlineData(PriorBookingState.CancelledPaid, false)]
    public async Task Create_PerUserLimitCountsOnlyOccupiedOrPaidSlots(PriorBookingState previous, bool allowed)
    {
        await using var db = TestDbContext.Create();
        var (travelerId, scheduleId) = await SeedPreviousRedemptionAsync(db, previous);
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var handler = new CreateTourBookingCommandHandler(db,
            new FakeCurrentUserService { UserId = travelerId, Role = nameof(UserRole.Traveler) },
            clock, new PendingBookingExpiryProcessor(db, clock, Options), Options, new FakePiiProtector());
        var command = new CreateTourBookingCommand(scheduleId.ToString(),
            [ParticipantRequest("New booking")], "SAVE10", Guid.NewGuid());

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().Be(allowed);
        if (!allowed) result.ErrorCode.Should().Be(TourBookingErrorCodes.VoucherUsageLimitReached);
    }

    [Theory]
    [InlineData(PriorBookingState.ExpiredUnpaid, true)]
    [InlineData(PriorBookingState.CancelledUnpaid, true)]
    [InlineData(PriorBookingState.PendingUnpaid, false)]
    [InlineData(PriorBookingState.CancelledPaid, false)]
    public async Task Quote_PerUserLimitCountsOnlyOccupiedOrPaidSlots(PriorBookingState previous, bool allowed)
    {
        await using var db = TestDbContext.Create();
        var (travelerId, scheduleId) = await SeedPreviousRedemptionAsync(db, previous);
        var handler = new QuoteTourBookingQueryHandler(db,
            new FakeCurrentUserService { UserId = travelerId, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = Now });
        var query = new QuoteTourBookingQuery(scheduleId.ToString(), new QuoteParticipantCounts(1, 0, 0), "SAVE10");

        var result = await handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().Be(allowed);
        if (!allowed) result.ErrorCode.Should().Be(TourBookingErrorCodes.VoucherUsageLimitReached);
    }

    private static async Task<(long TravelerId, long ScheduleId)> SeedPreviousRedemptionAsync(
        TestDbContext db, PriorBookingState previous)
    {
        var traveler = await TourBookingTestData.SeedTravelerAsync(db, 1);
        var tour = TourBookingTestData.SeedTour(2, 1_000_000m);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        var schedule = TourBookingTestData.SeedSchedule(tour, Now.AddDays(5), 10, 0);
        db.TourSchedules.Add(schedule);
        var voucher = TourBookingTestData.SeedVoucher("SAVE10", VoucherDiscountType.Percentage,
            10m, 500_000m, 0m, 5, 1, Now.AddDays(-1), Now.AddDays(2),
            previous is PriorBookingState.ExpiredUnpaid or PriorBookingState.CancelledUnpaid ? 0 : 1, [tour]);
        db.Vouchers.Add(voucher);
        await db.SaveChangesAsync();

        var booking = Booking.Create("BK-20261025-00001", traveler.Id, schedule.Id, voucher.Id,
            1_000_000m, 100_000m, 900_000m,
            [BookingParticipant.Create("Original booking", ParticipantType.Adult, null, null, null, null, null)],
            Now.AddMinutes(-20), Now.AddMinutes(10));
        switch (previous)
        {
            case PriorBookingState.ExpiredUnpaid:
                // Exercise the persisted terminal state while the handler's clock remains earlier.
                TourBookingTestData.Set(booking, nameof(Booking.PaymentExpiresAtUtc), Now.AddMinutes(-10));
                booking.ExpirePendingPayment(Now);
                break;
            case PriorBookingState.CancelledUnpaid:
                booking.Cancel(Now.AddMinutes(-5), "Cancelled before payment");
                break;
            case PriorBookingState.CancelledPaid:
                booking.MarkPaid(Now.AddMinutes(-5));
                booking.Cancel(Now.AddMinutes(-4), "Cancelled after payment");
                break;
        }
        db.Bookings.Add(booking);
        db.VoucherRedemptions.Add(VoucherRedemption.Create(voucher, booking, 100_000m, Now.AddMinutes(-20)));
        await db.SaveChangesAsync();
        return (traveler.Id, schedule.Id);
    }

    private static CreateTourBookingParticipantRequest ParticipantRequest(string name) =>
        new(name, ParticipantType.Adult, null, null, null, null, null);

    public enum PriorBookingState
    {
        ExpiredUnpaid,
        CancelledUnpaid,
        PendingUnpaid,
        CancelledPaid,
    }
}