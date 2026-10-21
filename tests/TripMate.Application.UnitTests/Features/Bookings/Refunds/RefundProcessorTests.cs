using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Bookings.Refunds;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Bookings.Refunds;

public sealed class RefundProcessorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProcessDueRefunds_WhenGatewayAccepts_RecordsRefundEvidenceAndMarksBookingRefunded()
    {
        await using TestDbContext db = CreateDbContext();
        Refund refund = await SeedRefundAsync(db);
        var gateway = new FakeRefundGateway(new RefundSubmissionResult(true, GatewayReference: "gw-refund-1"));
        var processor = new RefundProcessor(db, new FixedClock(Now), new FakeRefundGatewayResolver(gateway),
            new RefundProcessingOptions());

        RefundSweepResult result = await processor.ProcessDueRefundsAsync(CancellationToken.None);

        Assert.Equal(1, result.ProcessedCount);
        Assert.Empty(result.Failures);
        Refund reloadedRefund = await db.Refunds.SingleAsync();
        Booking reloadedBooking = await db.Bookings.SingleAsync();
        PaymentTransaction refundTransaction = await db.PaymentTransactions
            .SingleAsync(transaction => transaction.TransactionType == PaymentTransaction.TypeRefund);
        Assert.Equal(RefundStatus.Processed, reloadedRefund.Status);
        Assert.Equal("gw-refund-1", reloadedRefund.GatewayReference);
        Assert.Equal(PaymentStatus.Refunded, reloadedBooking.PaymentStatus);
        Assert.Equal(PaymentTransactionStatus.Success, refundTransaction.Status);
        Assert.Equal(reloadedRefund.Id, refundTransaction.RefundId);
        Assert.Equal($"tripmate-refund-{refund.Id}", gateway.LastRequest!.IdempotencyKey);
    }

    [Fact]
    public async Task ProcessDueRefunds_WhenGatewayIsTemporarilyUnavailable_SchedulesRetryWithoutDuplicateTransaction()
    {
        await using TestDbContext db = CreateDbContext();
        await SeedRefundAsync(db);
        var gateway = new FakeRefundGateway(new RefundSubmissionResult(false,
            FailureCode: "payment.refund.gateway_unavailable", FailureMessage: "Timed out", IsRetryable: true));
        var processor = new RefundProcessor(db, new FixedClock(Now), new FakeRefundGatewayResolver(gateway),
            new RefundProcessingOptions { RetryDelay = TimeSpan.FromMinutes(3) });

        RefundSweepResult result = await processor.ProcessDueRefundsAsync(CancellationToken.None);

        Assert.Equal(0, result.ProcessedCount);
        Assert.Equal(1, result.DeferredCount);
        Refund refund = await db.Refunds.SingleAsync();
        Assert.Equal(RefundStatus.RetryPending, refund.Status);
        Assert.Equal(Now.AddMinutes(3), refund.NextAttemptAtUtc);
        Assert.Equal(1, refund.AttemptCount);
        PaymentTransaction transaction = await db.PaymentTransactions
            .SingleAsync(candidate => candidate.TransactionType == PaymentTransaction.TypeRefund);
        Assert.Equal(PaymentTransactionStatus.Failed, transaction.Status);
    }

    [Fact]
    public async Task ProcessDueRefunds_WhenCurrentCheckoutGatewayDiffers_UsesOriginalPaymentGateway()
    {
        await using TestDbContext db = CreateDbContext();
        await SeedRefundAsync(db, PaymentGateway.PayOS);
        var configuredForNewCheckouts = new FakeRefundGateway(new RefundSubmissionResult(true, GatewayReference: "mock"));
        var originalGateway = new FakeRefundGateway(new RefundSubmissionResult(true, GatewayReference: "payos-refund"),
            PaymentGateway.PayOS);
        var processor = new RefundProcessor(db, new FixedClock(Now),
            new FakeRefundGatewayResolver(configuredForNewCheckouts, originalGateway), new RefundProcessingOptions());

        RefundSweepResult result = await processor.ProcessDueRefundsAsync(CancellationToken.None);

        Assert.Equal(1, result.ProcessedCount);
        Assert.Null(configuredForNewCheckouts.LastRequest);
        Assert.NotNull(originalGateway.LastRequest);
        Refund refund = await db.Refunds.SingleAsync();
        Assert.Equal(RefundStatus.Processed, refund.Status);
    }

    [Fact]
    public async Task ProcessDueRefunds_WhenGatewayRequiresManualReview_DoesNotRecordRefundAsFailed()
    {
        await using TestDbContext db = CreateDbContext();
        await SeedRefundAsync(db, PaymentGateway.PayOS);
        var gateway = new FakeRefundGateway(new RefundSubmissionResult(false,
            FailureCode: "payment.refund.manual_review_required",
            FailureMessage: "Provider requires operations settlement.",
            IsRetryable: false,
            RequiresManualReview: true), PaymentGateway.PayOS);
        var processor = new RefundProcessor(db, new FixedClock(Now), new FakeRefundGatewayResolver(gateway),
            new RefundProcessingOptions());

        RefundSweepResult result = await processor.ProcessDueRefundsAsync(CancellationToken.None);

        Assert.Equal(0, result.ProcessedCount);
        Assert.Equal(1, result.DeferredCount);
        Refund refund = await db.Refunds.SingleAsync();
        Assert.Equal(RefundStatus.ManualReview, refund.Status);
        Assert.Equal("payment.refund.manual_review_required", refund.LastErrorCode);
        Assert.Empty(await db.PaymentTransactions
            .Where(transaction => transaction.TransactionType == PaymentTransaction.TypeRefund)
            .ToListAsync());
    }

    [Fact]
    public async Task CompleteManualRefund_WhenAdministratorProvidesSettlementReference_RecordsImmutableEvidence()
    {
        await using TestDbContext db = CreateDbContext();
        await SeedRefundAsync(db, PaymentGateway.PayOS);
        var manualGateway = new FakeRefundGateway(new RefundSubmissionResult(false,
            FailureCode: "payment.refund.manual_review_required", RequiresManualReview: true), PaymentGateway.PayOS);
        var processor = new RefundProcessor(db, new FixedClock(Now), new FakeRefundGatewayResolver(manualGateway),
            new RefundProcessingOptions());
        await processor.ProcessDueRefundsAsync(CancellationToken.None);

        var administrator = new User
        {
            Role = UserRole.Administrator,
            Status = AccountStatus.Active,
            FullName = "Refund administrator",
        };
        db.Users.Add(administrator);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = administrator.Id, Role = nameof(UserRole.Administrator) };
        var handler = new CompleteManualRefundCommandHandler(db, currentUser, new FixedClock(Now.AddMinutes(5)));

        var key = Guid.NewGuid();
        var result = await handler.Handle(new CompleteManualRefundCommand((await db.Refunds.SingleAsync()).Id, key,
            "PAYOS-MANUAL-REF-1"), CancellationToken.None);
        var replay = await handler.Handle(new CompleteManualRefundCommand((await db.Refunds.SingleAsync()).Id, key,
            "PAYOS-MANUAL-REF-1"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Should().BeEquivalentTo(result.Value);
        (await db.Refunds.SingleAsync()).Status.Should().Be(RefundStatus.Processed);
        (await db.PaymentTransactions.SingleAsync(item => item.TransactionType == PaymentTransaction.TypeRefund)).Status
            .Should().Be(PaymentTransactionStatus.Success);
        (await db.Bookings.SingleAsync()).PaymentStatus.Should().Be(PaymentStatus.Refunded);
        (await db.RefundManualCompletionOperations.CountAsync()).Should().Be(1);
    }

    private static TestDbContext CreateDbContext() => new(new DbContextOptionsBuilder<TestDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private static async Task<Refund> SeedRefundAsync(TestDbContext db, PaymentGateway gateway = PaymentGateway.Mock)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), 22L);
        Set(tour, nameof(Tour.Title), "Refund processor tour");
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
        Set(booking, nameof(Booking.BookingCode), "BK-REFUND-PROCESSOR");
        Set(booking, nameof(Booking.TourSchedule), schedule);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        PaymentTransaction payment = PaymentTransaction.Create(booking.Id, gateway, 500_000m,
            orderCode: 1, createdAtUtc: Now);
        payment.MarkSuccess("payment-ref-1", "{}", Now, 0m);
        db.PaymentTransactions.Add(payment);
        await db.SaveChangesAsync();

        Refund refund = Refund.CreatePending(booking.Id, payment.Id, cancellationOperationId: 1, 500_000m,
            "Traveler request", RefundInitiator.System, Now);
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

    private sealed class FakeRefundGateway(RefundSubmissionResult result, PaymentGateway paymentGateway = PaymentGateway.Mock)
        : IPaymentGateway
    {
        public PaymentGateway Gateway => paymentGateway;
        public RefundSubmissionRequest? LastRequest { get; private set; }

        public Task<PaymentLinkResult> CreatePaymentLinkAsync(PaymentLinkRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RefundSubmissionResult> SubmitRefundAsync(RefundSubmissionRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeRefundGatewayResolver(params IPaymentGateway[] gateways) : IRefundPaymentGatewayResolver
    {
        public IPaymentGateway? Resolve(PaymentGateway gateway) => gateways.SingleOrDefault(candidate =>
            candidate.Gateway == gateway);
    }
}
