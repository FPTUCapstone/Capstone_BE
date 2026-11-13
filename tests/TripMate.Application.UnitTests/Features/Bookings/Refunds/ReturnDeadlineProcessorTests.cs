using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Bookings.Refunds;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Bookings.Refunds;

// BR-144 (SRS 3.8.4.3, spec v1.2): an unreturned Tour Operator share becomes a pending debt once
// its 3-working-day return deadline passes, offset against the operator's next settlements
// (already wired on develop by DAILY-PAYOUT-TRANSFER) — this only records the debt itself.
public sealed class ReturnDeadlineProcessorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RecordOverdueDebts_WhenDeadlineNotYetReached_LeavesRefundUntouched()
    {
        TestDbContext db = TestDbContext.Create();
        long operatorUserId = 22;
        Refund refund = await SeedAwaitingPartnerReturnRefundAsync(db, operatorUserId, share: 350_000m,
            returnDeadlineAtUtc: Now.AddDays(1));
        var processor = new ReturnDeadlineProcessor(db, new FixedClock(Now), new ReturnDeadlineOptions());

        int recordedCount = await processor.RecordOverdueDebtsAsync(CancellationToken.None);

        recordedCount.Should().Be(0);
        (await db.Refunds.SingleAsync()).DebtRecordedAtUtc.Should().BeNull();
        (await db.TourOperatorDebts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RecordOverdueDebts_WhenDeadlinePassedStillAwaitingPartnerReturn_RecordsDebtForFullShare()
    {
        TestDbContext db = TestDbContext.Create();
        long operatorUserId = 22;
        Refund refund = await SeedAwaitingPartnerReturnRefundAsync(db, operatorUserId, share: 350_000m,
            returnDeadlineAtUtc: Now.AddDays(-1));
        var processor = new ReturnDeadlineProcessor(db, new FixedClock(Now), new ReturnDeadlineOptions());

        int recordedCount = await processor.RecordOverdueDebtsAsync(CancellationToken.None);

        recordedCount.Should().Be(1);
        Refund reloaded = await db.Refunds.SingleAsync();
        reloaded.Status.Should().Be(RefundStatus.AwaitingPartnerReturn, "the refund still waits for the actual return");
        reloaded.DebtRecordedAtUtc.Should().Be(Now);
        TourOperatorDebt debt = await db.TourOperatorDebts.SingleAsync();
        debt.TourOperatorUserId.Should().Be(operatorUserId);
        debt.Amount.Should().Be(350_000m);
        debt.OutstandingAmount.Should().Be(350_000m);
        (await db.AuditLogs.SingleAsync()).ActionType.Should().Be("RefundOperatorDebtRecorded");
    }

    [Fact]
    public async Task RecordOverdueDebts_WhenDeadlinePassedWithPartialReturnReported_StillRecordsDebt()
    {
        // 7.a1: only part of the share was credited — the refund stays ReturnReported, and the
        // unreturned amount still becomes a pending debt.
        TestDbContext db = TestDbContext.Create();
        long operatorUserId = 22;
        Refund refund = await SeedAwaitingPartnerReturnRefundAsync(db, operatorUserId, share: 350_000m,
            returnDeadlineAtUtc: Now.AddDays(-1));
        refund.ReportReturn("VCB-PARTIAL", Now.AddDays(-2));
        await db.SaveChangesAsync();
        var processor = new ReturnDeadlineProcessor(db, new FixedClock(Now), new ReturnDeadlineOptions());

        int recordedCount = await processor.RecordOverdueDebtsAsync(CancellationToken.None);

        recordedCount.Should().Be(1);
        (await db.Refunds.SingleAsync()).Status.Should().Be(RefundStatus.ReturnReported);
        (await db.TourOperatorDebts.SingleAsync()).Amount.Should().Be(350_000m);
    }

    [Fact]
    public async Task RecordOverdueDebts_AlreadyRecorded_IsNeverRecordedTwice()
    {
        TestDbContext db = TestDbContext.Create();
        long operatorUserId = 22;
        Refund refund = await SeedAwaitingPartnerReturnRefundAsync(db, operatorUserId, share: 350_000m,
            returnDeadlineAtUtc: Now.AddDays(-1));
        refund.RecordOperatorDebt(Now.AddHours(-1));
        await db.SaveChangesAsync();
        var processor = new ReturnDeadlineProcessor(db, new FixedClock(Now), new ReturnDeadlineOptions());

        int recordedCount = await processor.RecordOverdueDebtsAsync(CancellationToken.None);

        recordedCount.Should().Be(0);
        (await db.TourOperatorDebts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RecordOverdueDebts_RefundAlreadyReceived_IsNeverTouched()
    {
        TestDbContext db = TestDbContext.Create();
        long operatorUserId = 22;
        Refund refund = await SeedAwaitingPartnerReturnRefundAsync(db, operatorUserId, share: 350_000m,
            returnDeadlineAtUtc: Now.AddDays(-1));
        refund.ReportReturn("VCB-FULL", Now.AddDays(-2));
        refund.RecordReturnReceived(Now.AddDays(-1));
        await db.SaveChangesAsync();
        var processor = new ReturnDeadlineProcessor(db, new FixedClock(Now), new ReturnDeadlineOptions());

        int recordedCount = await processor.RecordOverdueDebtsAsync(CancellationToken.None);

        recordedCount.Should().Be(0);
        (await db.TourOperatorDebts.CountAsync()).Should().Be(0);
    }

    private static async Task<Refund> SeedAwaitingPartnerReturnRefundAsync(TestDbContext db, long operatorUserId,
        decimal share, DateTimeOffset returnDeadlineAtUtc, decimal amount = 500_000m)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), operatorUserId);
        Set(tour, nameof(Tour.Title), "BR-144 return-deadline tour");
        Set(tour, nameof(Tour.BasePrice), 1_000_000m);
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
            totalAmount: 1_000_000m, BookingStatus.Cancelled, PaymentStatus.Paid);
        Set(booking, nameof(Booking.BookingCode), "BK-RETURNDEADLINE");
        Set(booking, nameof(Booking.TourSchedule), schedule);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        PaymentTransaction payment = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock, amount,
            orderCode: 4, createdAtUtc: Now.AddDays(-10));
        payment.MarkSuccess("payment-ref-returndeadline", "{}", Now.AddDays(-10), 0m);
        db.PaymentTransactions.Add(payment);
        await db.SaveChangesAsync();

        Refund refund = Refund.CreatePendingOperatorConfirmation(booking.Id, payment.Id, cancellationOperationId: 1,
            amount, "Traveler request", RefundInitiator.System, Now.AddDays(-10));
        refund.Confirm(share, returnDeadlineAtUtc, Now.AddDays(-5));
        db.Refunds.Add(refund);
        await db.SaveChangesAsync();
        return refund;
    }

    private static void Set<T>(object instance, string property, T value) => instance.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .SetValue(instance, value);

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }
}