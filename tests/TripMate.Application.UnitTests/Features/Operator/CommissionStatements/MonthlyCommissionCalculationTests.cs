using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Operator.CommissionStatements;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.CommissionStatements;

public sealed class MonthlyCommissionCalculationTests
{
    [Fact]
    public async Task SuccessfulForeignCurrencyPaymentCannotBeBilledAsVnd()
    {
        await using var db = Db();
        await Owner(db);
        await Booking(db, At(9), currency: "USD");
        var result = await Handler(db).Handle(new(10, "2026-09"), default);
        Assert.Equal("commission.no_eligible_base", result.ErrorCode);
        Assert.Empty(db.DirectCommissionLedgerEntries);
        Assert.Empty(db.DirectCommissionStatements);
    }
    private static readonly DateTimeOffset Now = new(2026, 12, 2, 0, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int month, int day = 15) => new(2026, month, day, 0, 0, 0, TimeSpan.Zero);
    private static TestDbContext Db() => new(new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static IssueDirectCommissionStatementCommandHandler Handler(TestDbContext db) => new(db,
        new FakeCurrentUserService { UserId = 99, Role = "Administrator" }, new FakeDateTimeProvider { UtcNow = Now });

    [Fact]
    public async Task BillsVerifiedCompletedRevenueAtVietnamMonthBoundaryExactlyOnce()
    {
        await using var db = Db();
        await Owner(db);
        var included = await Booking(db, new(2026, 9, 30, 16, 59, 59, TimeSpan.Zero));
        await Booking(db, new(2026, 9, 30, 17, 0, 0, TimeSpan.Zero)); // October in Vietnam.
        await Booking(db, At(9), paid: false);
        await Booking(db, At(9), status: BookingStatus.PendingPayment);
        var cancelled = await Booking(db, At(9), status: BookingStatus.Confirmed);
        Set(await db.TourSchedules.SingleAsync(s => s.Id == cancelled.TourScheduleId), nameof(TourSchedule.Status), TourScheduleStatus.Cancelled);
        var stopped = await Booking(db, At(9), status: BookingStatus.Confirmed);
        Set(await db.TourSchedules.SingleAsync(s => s.Id == stopped.TourScheduleId), nameof(TourSchedule.Status), TourScheduleStatus.EmergencyStopped);
        await db.SaveChangesAsync();
        var result = await Handler(db).Handle(new(10, "2026-09"), default);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        var statement = await db.DirectCommissionStatements.SingleAsync();
        Assert.Equal(10000, statement.CommissionAmountVnd);
        Assert.Equal(included.Id, Assert.Single(db.DirectCommissionLedgerEntries).BookingId);
        Assert.True((await Handler(db).Handle(new(10, "2026-09"), default)).IsFailure);
        Assert.Single(db.DirectCommissionLedgerEntries);
        Assert.True((await Handler(db).Handle(new(10, "2026-10"), default)).IsSuccess);
        Assert.Equal(2, await db.DirectCommissionLedgerEntries.CountAsync());
    }

    [Fact]
    public async Task HeldRefundIsCarriedWhenResolvedWithoutLosingRevenueOrDoubleBilling()
    {
        await using var db = Db();
        await Owner(db);
        var booking = await Booking(db, At(9));
        var refund = DirectRefundCase.Create(booking.Id, DirectRefundCase.SourcePostTripComplaint, 20000, 100000, At(9), 1);
        db.DirectRefundCases.Add(refund);
        await db.SaveChangesAsync();
        Assert.True((await Handler(db).Handle(new(10, "2026-09"), default)).IsSuccess);
        var held = await db.DirectCommissionCalculations.SingleAsync();
        Assert.Equal(1, held.HeldBookingCount);
        Assert.Equal(0, (await db.DirectCommissionStatements.SingleAsync()).CommissionAmountVnd);
        Assert.Empty(db.DirectCommissionLedgerEntries);
        refund.Complete("TravelerConfirmed", null, At(10));
        await db.SaveChangesAsync();
        Assert.True((await Handler(db).Handle(new(10, "2026-10"), default)).IsSuccess);
        var october = await db.DirectCommissionStatements.SingleAsync(s => s.Period == "2026-10");
        Assert.Equal(8000, october.CommissionAmountVnd);
        Assert.Equal(2, await db.DirectCommissionLedgerEntries.CountAsync());
        Assert.True((await Handler(db).Handle(new(10, "2026-11"), default)).IsFailure);
        Assert.Equal(2, await db.DirectCommissionLedgerEntries.CountAsync());
    }

    [Fact]
    public async Task LateRefundUsesOriginalRateAndCreditSurvivesUntilFutureRevenueConsumesIt()
    {
        await using var db = Db();
        await Owner(db);
        var booking = await Booking(db, At(9));
        Assert.True((await Handler(db).Handle(new(10, "2026-09"), default)).IsSuccess);
        var original = await db.DirectCommissionStatements.SingleAsync();
        var refund = DirectRefundCase.Create(booking.Id, DirectRefundCase.SourcePostTripComplaint, 100000, 100000, At(10), 1);
        refund.Complete("TravelerConfirmed", null, At(10));
        db.DirectRefundCases.Add(refund);
        (await db.OperatorProfiles.SingleAsync()).CommissionRate = 20;
        await db.SaveChangesAsync();
        await Booking(db, At(10), amount: 20000);
        Assert.True((await Handler(db).Handle(new(10, "2026-10"), default)).IsSuccess);
        var october = await db.DirectCommissionStatements.SingleAsync(s => s.Period == "2026-10");
        var calculation = await db.DirectCommissionCalculations.SingleAsync(c => c.StatementId == october.Id);
        Assert.Equal(0, october.CommissionAmountVnd);
        Assert.Equal(10000, calculation.RefundCommissionCreditVnd);
        Assert.Equal(6000, calculation.CreditCarriedForwardVnd);
        Assert.Equal(10000, original.CommissionAmountVnd); // Issued snapshot never changes.
        await Booking(db, At(11), amount: 40000);
        Assert.True((await Handler(db).Handle(new(10, "2026-11"), default)).IsSuccess);
        var november = await db.DirectCommissionStatements.SingleAsync(s => s.Period == "2026-11");
        Assert.Equal(2000, november.CommissionAmountVnd);
        Assert.Equal(0, (await db.DirectCommissionCalculations.SingleAsync(c => c.StatementId == november.Id)).CreditCarriedForwardVnd);
    }

    [Fact]
    public async Task LegacyCohortsAreHeldForReviewInsteadOfSilentlyRecharged()
    {
        await using var db = Db();
        await Owner(db);
        await Booking(db, At(9));
        db.DirectCommissionStatements.Add(DirectCommissionStatement.Issue("OLD-SEP", 10, "2026-09", 100000, .1m, 10000, new(2026, 10, 1), At(10, 1)));
        await db.SaveChangesAsync();
        Assert.True((await Handler(db).Handle(new(10, "2026-10"), default)).IsSuccess);
        var statement = await db.DirectCommissionStatements.SingleAsync(s => s.Period == "2026-10");
        Assert.Equal(0, statement.CommissionAmountVnd);
        Assert.Equal(1, (await db.DirectCommissionCalculations.SingleAsync()).AmbiguousLegacyBookingCount);
        Assert.Empty(db.DirectCommissionLedgerEntries);
    }

    [Fact]
    public async Task TinyBookingsUseAggregateHalfUpAndFullRefundNeverExceedsAllocatedFee()
    {
        await using var db = Db();
        await Owner(db);
        var first = await Booking(db, At(9), amount: 5);
        var second = await Booking(db, At(9), amount: 5);
        Assert.True((await Handler(db).Handle(new(10, "2026-09"), default)).IsSuccess);
        Assert.Equal(1, (await db.DirectCommissionStatements.SingleAsync()).CommissionAmountVnd);
        foreach (var booking in new[] { first, second })
        {
            var refund = DirectRefundCase.Create(booking.Id, DirectRefundCase.SourcePostTripComplaint, 5, 5, At(10), booking.Id);
            refund.Complete("TravelerConfirmed", null, At(10));
            db.DirectRefundCases.Add(refund);
        }
        await db.SaveChangesAsync();
        Assert.True((await Handler(db).Handle(new(10, "2026-10"), default)).IsSuccess);
        var october = await db.DirectCommissionStatements.SingleAsync(s => s.Period == "2026-10");
        Assert.Equal(1, (await db.DirectCommissionCalculations.SingleAsync(c => c.StatementId == october.Id)).CreditCarriedForwardVnd);
        Assert.Equal(-1, await db.DirectCommissionLedgerEntries.Where(e => e.SourceType != DirectCommissionLedgerEntry.BookingRevenue).SumAsync(e => e.CommissionAmountVnd));
    }

    [Fact]
    public async Task UnresolvedComplaintHoldsRevenueUntilItsActualResolutionMonth()
    {
        await using var db = Db();
        await Owner(db);
        var booking = await Booking(db, At(9));
        var complaint = PostTripComplaint.Create(booking.Id, 20, PostTripComplaintReasonCategory.Other, "Disputed service", At(9), At(9).AddDays(3), 100000);
        Set(complaint, nameof(PostTripComplaint.Status), PostTripComplaintStatus.Declined);
        db.PostTripComplaints.Add(complaint);
        await db.SaveChangesAsync();
        Assert.True((await Handler(db).Handle(new(10, "2026-09"), default)).IsSuccess);
        Assert.Equal(1, (await db.DirectCommissionCalculations.SingleAsync()).HeldBookingCount);
        Set(complaint, nameof(PostTripComplaint.Status), PostTripComplaintStatus.Rejected);
        Set(complaint, nameof(PostTripComplaint.RespondedAtUtc), At(10));
        await db.SaveChangesAsync();
        Assert.True((await Handler(db).Handle(new(10, "2026-10"), default)).IsSuccess);
        Assert.Equal(10000, (await db.DirectCommissionStatements.SingleAsync(s => s.Period == "2026-10")).CommissionAmountVnd);
    }

    [Fact]
    public async Task OpenFutureAndEarlierPeriodsAreRejected()
    {
        await using var db = Db();
        await Owner(db);
        Assert.True((await Handler(db).Handle(new(10, "2026-12"), default)).IsFailure);
        Assert.True((await Handler(db).Handle(new(10, "2027-01"), default)).IsFailure);
        await Booking(db, At(10));
        Assert.True((await Handler(db).Handle(new(10, "2026-10"), default)).IsSuccess);
        Assert.True((await Handler(db).Handle(new(10, "2026-09"), default)).IsFailure);
    }

    private static async Task Owner(TestDbContext db)
    {
        db.OperatorProfiles.Add(new OperatorProfile { UserId = 10, CommissionRate = 10 });
        await db.SaveChangesAsync();
    }

    private static async Task<TripMate.Domain.Entities.Booking> Booking(TestDbContext db, DateTimeOffset end, long amount = 100000,
        bool paid = true, BookingStatus status = BookingStatus.Completed, string currency = PaymentTransaction.DefaultCurrency)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), true)!;
        Set(tour, nameof(Tour.OperatorUserId), 10L); Set(tour, nameof(Tour.Title), "Test tour");
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        var schedule = TourSchedule.Create(end.AddHours(-2), end, "Test meeting", 10, end.AddDays(-5));
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();
        var booking = TripMate.Domain.Entities.Booking.Create(20, schedule.Id, 1, amount, status, paid ? PaymentStatus.Paid : PaymentStatus.Unpaid);
        Set(booking, nameof(TripMate.Domain.Entities.Booking.PaymentFlow), PaymentFlow.OperatorDirect);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        if (paid)
        {
            var payment = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock, amount, booking.Id, end.AddDays(-1), currency: currency);
            payment.MarkSuccess("TEST", null, end.AddDays(-1), .1m);
            db.PaymentTransactions.Add(payment);
            await db.SaveChangesAsync();
        }
        return booking;
    }
    private static void Set(object value, string property, object data) => value.GetType().GetProperty(property)!.SetValue(value, data);
}