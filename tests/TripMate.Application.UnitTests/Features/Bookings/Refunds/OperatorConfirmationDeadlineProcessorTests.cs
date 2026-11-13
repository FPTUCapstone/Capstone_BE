using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Bookings.Refunds;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Bookings.Refunds;

// BR-187 (SRS 3.8.4.3, spec v1.2, owner decision 13/11/2026): a refund request the Tour Operator
// has not confirmed within 3 working days of its creation is confirmed automatically.
public sealed class OperatorConfirmationDeadlineProcessorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 8, 0, 0, TimeSpan.Zero); // Friday

    [Fact]
    public async Task AutoConfirm_WhenDeadlineNotYetReached_LeavesRefundUntouched()
    {
        // Requested Wednesday 11/11; 3 working days later is Monday 16/11 — "Now" is before that.
        TestDbContext db = TestDbContext.Create();
        long operatorUserId = 22;
        Refund refund = await SeedRefundAsync(db, operatorUserId, requestedAtUtc: new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero));
        var processor = new OperatorConfirmationDeadlineProcessor(db, new FixedClock(Now), new OperatorConfirmationDeadlineOptions());

        int confirmedCount = await processor.AutoConfirmOverdueRefundsAsync(CancellationToken.None);

        confirmedCount.Should().Be(0);
        (await db.Refunds.SingleAsync()).Status.Should().Be(RefundStatus.PendingOperatorConfirmation);
        (await db.Notifications.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AutoConfirm_WhenDeadlinePassedWithNoSettlementTransferred_SkipsStraightToPendingAndNotifiesOperator()
    {
        // Requested Friday 06/11; 3 working days later is Wednesday 11/11 — well before "Now".
        TestDbContext db = TestDbContext.Create();
        long operatorUserId = 22;
        Refund refund = await SeedRefundAsync(db, operatorUserId, requestedAtUtc: new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero));
        var processor = new OperatorConfirmationDeadlineProcessor(db, new FixedClock(Now), new OperatorConfirmationDeadlineOptions());

        int confirmedCount = await processor.AutoConfirmOverdueRefundsAsync(CancellationToken.None);

        confirmedCount.Should().Be(1);
        Refund reloaded = await db.Refunds.SingleAsync();
        reloaded.Status.Should().Be(RefundStatus.Pending);
        reloaded.OperatorReturnShare.Should().Be(0m);
        reloaded.ReturnDeadlineAtUtc.Should().BeNull();
        reloaded.NextAttemptAtUtc.Should().Be(Now);
        (await db.AuditLogs.SingleAsync()).ActionType.Should().Be("RefundAutoConfirmed");
        Notification notification = await db.Notifications.SingleAsync();
        notification.UserId.Should().Be(operatorUserId);
        notification.Type.Should().Be("RefundAutoConfirmed");
    }

    [Fact]
    public async Task AutoConfirm_WhenDeadlinePassedWithSettledAdvance_MovesToAwaitingPartnerReturn()
    {
        TestDbContext db = TestDbContext.Create();
        long operatorUserId = 22;
        Refund refund = await SeedRefundAsync(db, operatorUserId,
            requestedAtUtc: new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero), amount: 500_000m);
        await SeedSettledAdvancePayoutAsync(db, refund.BookingId, operatorUserId, payoutAmount: 700_000m);
        var processor = new OperatorConfirmationDeadlineProcessor(db, new FixedClock(Now), new OperatorConfirmationDeadlineOptions());

        int confirmedCount = await processor.AutoConfirmOverdueRefundsAsync(CancellationToken.None);

        confirmedCount.Should().Be(1);
        Refund reloaded = await db.Refunds.SingleAsync();
        reloaded.Status.Should().Be(RefundStatus.AwaitingPartnerReturn);
        reloaded.OperatorReturnShare.Should().Be(350_000m);
        reloaded.ReturnDeadlineAtUtc.Should().Be(new DateTimeOffset(2026, 11, 18, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task AutoConfirm_RefundAlreadyPastConfirmation_IsNeverTouchedAgain()
    {
        TestDbContext db = TestDbContext.Create();
        long operatorUserId = 22;
        Refund refund = await SeedRefundAsync(db, operatorUserId, requestedAtUtc: new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero));
        refund.Confirm(0m, null, Now.AddDays(-1));
        await db.SaveChangesAsync();
        var processor = new OperatorConfirmationDeadlineProcessor(db, new FixedClock(Now), new OperatorConfirmationDeadlineOptions());

        int confirmedCount = await processor.AutoConfirmOverdueRefundsAsync(CancellationToken.None);

        confirmedCount.Should().Be(0);
        (await db.AuditLogs.CountAsync()).Should().Be(0);
    }

    private static async Task<Refund> SeedRefundAsync(TestDbContext db, long operatorUserId, DateTimeOffset requestedAtUtc,
        decimal amount = 500_000m, decimal bookingTotalAmount = 1_000_000m)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), operatorUserId);
        Set(tour, nameof(Tour.Title), "BR-187 auto-confirm tour");
        Set(tour, nameof(Tour.BasePrice), bookingTotalAmount);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Approved);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();

        var schedule = (TourSchedule)Activator.CreateInstance(typeof(TourSchedule), nonPublic: true)!;
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        Set(schedule, nameof(TourSchedule.StartAtUtc), Now.AddDays(10));
        Set(schedule, nameof(TourSchedule.EndAtUtc), Now.AddDays(10).AddHours(8));
        Set(schedule, nameof(TourSchedule.TotalCapacity), 10);
        Set(schedule, nameof(TourSchedule.Status), TourScheduleStatus.Scheduled);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();

        Booking booking = Booking.Create(travelerUserId: 11, tourScheduleId: schedule.Id, quantity: 1,
            totalAmount: bookingTotalAmount, BookingStatus.Cancelled, PaymentStatus.Paid);
        Set(booking, nameof(Booking.BookingCode), "BK-AUTOCONFIRM");
        Set(booking, nameof(Booking.TourSchedule), schedule);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        PaymentTransaction payment = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock, amount,
            orderCode: 3, createdAtUtc: requestedAtUtc);
        payment.MarkSuccess("payment-ref-autoconfirm", "{}", requestedAtUtc, 0m);
        db.PaymentTransactions.Add(payment);
        await db.SaveChangesAsync();

        Refund refund = Refund.CreatePendingOperatorConfirmation(booking.Id, payment.Id, cancellationOperationId: 1,
            amount, "Traveler request", RefundInitiator.System, requestedAtUtc);
        db.Refunds.Add(refund);
        await db.SaveChangesAsync();
        return refund;
    }

    private static async Task SeedSettledAdvancePayoutAsync(TestDbContext db, long bookingId, long operatorUserId,
        decimal payoutAmount)
    {
        var runDate = DateOnly.FromDateTime(Now.UtcDateTime.AddDays(-3));
        var batch = PayoutBatch.Create(runDate, operatorUserId, Now.AddDays(-3));
        db.PayoutBatches.Add(batch);
        await db.SaveChangesAsync();

        var settlement = PayoutSettlement.Create(batch.Id, bookingId, operatorUserId, runDate,
            PayoutSettlementKind.Advance, grossAmount: payoutAmount, commissionRate: 0m, commissionAmount: 0m,
            netAmount: payoutAmount, debtOffsetAmount: 0m, payoutAmount: payoutAmount, Now.AddDays(-3));
        db.PayoutSettlements.Add(settlement);
        batch.AddSettlement(settlement);
        batch.MarkSettled("PAYOUT-TEST-REF", Now.AddDays(-3));
        await db.SaveChangesAsync();
    }

    private static void Set<T>(object instance, string property, T value) => instance.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .SetValue(instance, value);

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }
}