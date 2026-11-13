using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Application.Features.Bookings.Refunds;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Bookings.Refunds;

// BR-161 (SRS 3.10.2, spec v1.2, Q2 owner decision 13/11/2026: 1 lần gửi đầu + 3 lần thử lại =
// tổng 4 lần gửi): a retryable gateway failure waits exactly RefundProcessingOptions.RetryDelay
// (1 hour) before trying again; once the 4th attempt still fails, the refund is Escalated rather
// than silently Failed, and every active Administrator is notified.
public sealed class RefundRetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProcessDueRefunds_OnRetryableGatewayFailure_WaitsTheConfiguredRetryDelay()
    {
        TestDbContext db = TestDbContext.Create();
        await SeedRefundAsync(db);
        var gateway = new FakeRefundGateway(new RefundSubmissionResult(false,
            FailureCode: "payment.refund.gateway_unavailable", IsRetryable: true));
        var processor = new RefundProcessor(db, new FixedClock(Now), new FakeRefundGatewayResolver(gateway),
            new RefundProcessingOptions());

        await processor.ProcessDueRefundsAsync(CancellationToken.None);

        Refund refund = await db.Refunds.SingleAsync();
        refund.Status.Should().Be(RefundStatus.RetryPending);
        refund.NextAttemptAtUtc.Should().Be(Now.AddHours(1), "RefundProcessingOptions.RetryDelay defaults to 1 hour (BR-161, Q2)");
        refund.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task ProcessDueRefunds_AfterTheFourthAttemptStillFails_EscalatesAndNotifiesAdministrators()
    {
        TestDbContext db = TestDbContext.Create();
        await SeedRefundAsync(db);
        long administratorId = await SeedActiveAdministratorAsync(db);
        var gateway = new FakeRefundGateway(new RefundSubmissionResult(false,
            FailureCode: "payment.refund.gateway_unavailable", IsRetryable: true));
        var options = new RefundProcessingOptions();
        var processor = new RefundProcessor(db, new FixedClock(Now), new FakeRefundGatewayResolver(gateway), options);

        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            // The fake clock never advances, so each loop iteration re-arms the due time itself
            // (the real NextAttemptAtUtc setter is private; this mirrors how every other test in
            // this suite already reaches past non-public entity state via reflection).
            Refund current = await db.Refunds.SingleAsync();
            Set(current, nameof(Refund.NextAttemptAtUtc), (DateTimeOffset?)Now);
            await db.SaveChangesAsync();
            await processor.ProcessDueRefundsAsync(CancellationToken.None);
        }

        Refund refund = await db.Refunds.SingleAsync();
        refund.Status.Should().Be(RefundStatus.Escalated);
        refund.AttemptCount.Should().Be(options.MaxAttempts);
        refund.NextAttemptAtUtc.Should().BeNull();
        (await db.Notifications.CountAsync(n => n.UserId == administratorId)).Should().Be(1);
    }

    [Fact]
    public async Task ProcessDueRefunds_WhenGatewayRequiresManualReview_NeverEscalatesInstead()
    {
        // Regression: the RequiresManualReview flag diverts immediately, independent of the
        // retry-attempt budget — it must never be swallowed by the new Escalate path.
        TestDbContext db = TestDbContext.Create();
        await SeedRefundAsync(db);
        var gateway = new FakeRefundGateway(new RefundSubmissionResult(false,
            FailureCode: "payment.refund.manual_review_required", IsRetryable: false, RequiresManualReview: true));
        var processor = new RefundProcessor(db, new FixedClock(Now), new FakeRefundGatewayResolver(gateway),
            new RefundProcessingOptions());

        await processor.ProcessDueRefundsAsync(CancellationToken.None);

        (await db.Refunds.SingleAsync()).Status.Should().Be(RefundStatus.ManualReview);
    }

    private static async Task<Refund> SeedRefundAsync(TestDbContext db)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), 22L);
        Set(tour, nameof(Tour.Title), "BR-161 retry policy tour");
        Set(tour, nameof(Tour.BasePrice), 500_000m);
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
            totalAmount: 500_000m, BookingStatus.Cancelled, PaymentStatus.Paid);
        Set(booking, nameof(Booking.BookingCode), "BK-RETRYPOLICY");
        Set(booking, nameof(Booking.TourSchedule), schedule);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        PaymentTransaction payment = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock, 500_000m,
            orderCode: 5, createdAtUtc: Now);
        payment.MarkSuccess("payment-ref-retrypolicy", "{}", Now, 0m);
        db.PaymentTransactions.Add(payment);
        await db.SaveChangesAsync();

        Refund refund = Refund.CreatePending(booking.Id, payment.Id, cancellationOperationId: 1, 500_000m,
            "Traveler request", RefundInitiator.System, Now);
        db.Refunds.Add(refund);
        await db.SaveChangesAsync();
        return refund;
    }

    private static async Task<long> SeedActiveAdministratorAsync(TestDbContext db)
    {
        var administrator = new User
        {
            Role = UserRole.Administrator,
            Status = AccountStatus.Active,
            FullName = "Refund retry administrator",
        };
        db.Users.Add(administrator);
        await db.SaveChangesAsync();
        return administrator.Id;
    }

    private static void Set<T>(object instance, string property, T value) => instance.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .SetValue(instance, value);

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class FakeRefundGateway(RefundSubmissionResult result, PaymentGateway paymentGateway = PaymentGateway.Mock)
        : IPaymentGateway
    {
        public PaymentGateway Gateway => paymentGateway;

        public Task<PaymentLinkResult> CreatePaymentLinkAsync(PaymentLinkRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RefundSubmissionResult> SubmitRefundAsync(RefundSubmissionRequest request,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class FakeRefundGatewayResolver(params IPaymentGateway[] gateways) : IRefundPaymentGatewayResolver
    {
        public IPaymentGateway? Resolve(PaymentGateway gateway) => gateways.SingleOrDefault(candidate =>
            candidate.Gateway == gateway);
    }
}