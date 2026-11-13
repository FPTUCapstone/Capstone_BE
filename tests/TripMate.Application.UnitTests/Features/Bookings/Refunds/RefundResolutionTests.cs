using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Application.Features.Bookings.Refunds;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Bookings.Refunds;

// Spec v1.2 §3.3 (POST /admin/refunds/{id}/resolve): an Escalated (BR-161) or Reconciliation
// (UC-41 6.b1) refund must have a way out — settled by an Administrator, or rejected — and a
// BR-144 debt must not survive the share it stands for being received (no double collection).
public sealed class RefundResolutionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private const long OperatorUserId = 22;

    [Theory]
    [InlineData(RefundStatus.Escalated)]
    [InlineData(RefundStatus.Reconciliation)]
    public async Task CompleteManual_FromAnAdministratorReviewStatus_MarksRefundProcessed(RefundStatus status)
    {
        TestDbContext db = TestDbContext.Create();
        Refund refund = await SeedRefundAsync(db, status);
        long administratorId = await SeedUserAsync(db, UserRole.Administrator);
        var handler = new CompleteManualRefundCommandHandler(db, new CurrentUser(administratorId, UserRole.Administrator),
            new FixedClock(Now), new NoOpLock());

        var result = await handler.Handle(new CompleteManualRefundCommand(refund.Id, Guid.NewGuid(), "BANK-REF-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await db.Refunds.SingleAsync()).Status.Should().Be(RefundStatus.Processed);
        (await db.Bookings.SingleAsync()).PaymentStatus.Should().Be(PaymentStatus.PartiallyRefunded);
    }

    [Theory]
    [InlineData(RefundStatus.Escalated)]
    [InlineData(RefundStatus.Reconciliation)]
    public async Task Reject_FromAnAdministratorReviewStatus_ClosesRefundAndNotifiesTraveler(RefundStatus status)
    {
        TestDbContext db = TestDbContext.Create();
        Refund refund = await SeedRefundAsync(db, status);
        long administratorId = await SeedUserAsync(db, UserRole.Administrator);
        var handler = new RejectReviewedRefundCommandHandler(db, new CurrentUser(administratorId, UserRole.Administrator),
            new FixedClock(Now));

        var result = await handler.Handle(new RejectReviewedRefundCommand(refund.Id, " Payment never settled "),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        Refund reloaded = await db.Refunds.SingleAsync();
        reloaded.Status.Should().Be(RefundStatus.Rejected);
        reloaded.LastErrorMessage.Should().Be("Payment never settled");
        (await db.Notifications.SingleAsync()).UserId.Should().Be(11);
        (await db.AuditLogs.SingleAsync()).ActionType.Should().Be(RejectReviewedRefundCommandHandler.AuditAction);
    }

    [Fact]
    public async Task Reject_RefundNotUnderAdministratorReview_IsConflict()
    {
        TestDbContext db = TestDbContext.Create();
        Refund refund = await SeedRefundAsync(db, RefundStatus.PendingOperatorConfirmation);
        long administratorId = await SeedUserAsync(db, UserRole.Administrator);
        var handler = new RejectReviewedRefundCommandHandler(db, new CurrentUser(administratorId, UserRole.Administrator),
            new FixedClock(Now));

        var result = await handler.Handle(new RejectReviewedRefundCommand(refund.Id, "No"), CancellationToken.None);

        result.ErrorCode.Should().Be(RefundConfirmationErrorCodes.RequestNotPending);
        (await db.Refunds.SingleAsync()).Status.Should().Be(RefundStatus.PendingOperatorConfirmation);
    }

    [Fact]
    public async Task Reject_WithoutReason_IsRejected()
    {
        TestDbContext db = TestDbContext.Create();
        Refund refund = await SeedRefundAsync(db, RefundStatus.Escalated);
        long administratorId = await SeedUserAsync(db, UserRole.Administrator);
        var handler = new RejectReviewedRefundCommandHandler(db, new CurrentUser(administratorId, UserRole.Administrator),
            new FixedClock(Now));

        var result = await handler.Handle(new RejectReviewedRefundCommand(refund.Id, "  "), CancellationToken.None);

        result.ErrorCode.Should().Be(BookingCancellationErrorCodes.ReasonRequired);
    }

    [Fact]
    public async Task Reject_ByStaff_IsUnauthorized()
    {
        TestDbContext db = TestDbContext.Create();
        Refund refund = await SeedRefundAsync(db, RefundStatus.Escalated);
        long staffId = await SeedUserAsync(db, UserRole.Staff);
        var handler = new RejectReviewedRefundCommandHandler(db, new CurrentUser(staffId, UserRole.Staff), new FixedClock(Now));

        var result = await handler.Handle(new RejectReviewedRefundCommand(refund.Id, "No"), CancellationToken.None);

        result.ErrorCode.Should().Be(BookingCancellationErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task ReturnDeadline_RecordsTheDebtAgainstItsRefund()
    {
        TestDbContext db = TestDbContext.Create();
        Refund refund = await SeedRefundAsync(db, RefundStatus.AwaitingPartnerReturn);

        await new ReturnDeadlineProcessor(db, new FixedClock(Now), new ReturnDeadlineOptions())
            .RecordOverdueDebtsAsync(CancellationToken.None);

        (await db.TourOperatorDebts.SingleAsync()).RefundId.Should().Be(refund.Id);
    }

    [Fact]
    public async Task ReturnReceived_AfterTheDebtWasRecorded_ClosesTheOutstandingDebt()
    {
        // BR-144: the share arrived by bank transfer after its deadline — the same amount must
        // not also be offset against the operator's next settlements.
        TestDbContext db = TestDbContext.Create();
        Refund refund = await SeedRefundAsync(db, RefundStatus.AwaitingPartnerReturn);
        await new ReturnDeadlineProcessor(db, new FixedClock(Now), new ReturnDeadlineOptions())
            .RecordOverdueDebtsAsync(CancellationToken.None);
        db.TourOperatorDebts.Add(TourOperatorDebt.Create(OperatorUserId, 90_000m, Now, Now)); // unrelated debt
        refund.ReportReturn("VCB-LATE", Now);
        await db.SaveChangesAsync();
        long staffId = await SeedUserAsync(db, UserRole.Staff);
        var handler = new RecordBookingRefundReturnReceivedCommandHandler(db, new CurrentUser(staffId, UserRole.Staff),
            new FixedClock(Now.AddHours(1)));

        var result = await handler.Handle(new RecordBookingRefundReturnReceivedCommand(refund.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        List<TourOperatorDebt> debts = await db.TourOperatorDebts.ToListAsync();
        debts.Single(debt => debt.RefundId == refund.Id).OutstandingAmount.Should().Be(0m);
        debts.Single(debt => debt.RefundId is null).OutstandingAmount.Should().Be(90_000m, "an unrelated debt is untouched");
    }

    private static async Task<Refund> SeedRefundAsync(TestDbContext db, RefundStatus status)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), OperatorUserId);
        Set(tour, nameof(Tour.Title), "Refund resolution tour");
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
        Set(booking, nameof(Booking.BookingCode), "BK-RESOLVE");
        Set(booking, nameof(Booking.TourSchedule), schedule);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        PaymentTransaction payment = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock, 1_000_000m,
            orderCode: 6, createdAtUtc: Now.AddDays(-10));
        payment.MarkSuccess("payment-ref-resolve", "{}", Now.AddDays(-10), 0m);
        db.PaymentTransactions.Add(payment);
        await db.SaveChangesAsync();

        Refund refund = status == RefundStatus.Reconciliation
            ? Refund.CreateReconciliation(booking.Id, payment.Id, 1, 500_000m, "Mismatch", RefundInitiator.System, Now.AddDays(-9))
            : Refund.CreatePendingOperatorConfirmation(booking.Id, payment.Id, 1, 500_000m, "Traveler request",
                RefundInitiator.System, Now.AddDays(-9));
        if (status == RefundStatus.AwaitingPartnerReturn)
        {
            refund.Confirm(350_000m, Now.AddDays(-1), Now.AddDays(-5));
        }
        else if (status == RefundStatus.Escalated)
        {
            refund.Confirm(0m, null, Now.AddDays(-5));
            refund.TryStartAttempt(Now.AddDays(-5));
            refund.Escalate("payment.refund.gateway_unavailable", "Gateway unreachable.");
        }

        db.Refunds.Add(refund);
        await db.SaveChangesAsync();
        return refund;
    }

    private static async Task<long> SeedUserAsync(TestDbContext db, UserRole role)
    {
        var user = new User { Role = role, Status = AccountStatus.Active, FullName = role.ToString() };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static void Set<T>(object instance, string property, T value) => instance.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .SetValue(instance, value);

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class CurrentUser(long userId, UserRole role) : ICurrentUserService
    {
        public long? UserId => userId;

        public string? Role => role.ToString();
    }

    private sealed class NoOpLock : IRefundManualCompletionLock
    {
        public Task AcquireAsync(long refundId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}