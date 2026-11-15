using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Bookings.Refunds;

public sealed class RefundLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PendingRefund_StartsAndCompletesExactlyOnce()
    {
        Refund refund = Create();

        Assert.True(refund.TryStartAttempt(Now));
        refund.MarkProcessed("gateway-ref-1", Now.AddSeconds(1));

        Assert.Equal(RefundStatus.Processed, refund.Status);
        Assert.Equal(1, refund.AttemptCount);
        Assert.Equal("gateway-ref-1", refund.GatewayReference);
        Assert.False(refund.TryStartAttempt(Now.AddMinutes(1)));
    }

    [Fact]
    public void Retry_IsNotEligibleBeforeItsDueTime()
    {
        Refund refund = Create();
        Assert.True(refund.TryStartAttempt(Now));
        refund.ScheduleRetry("gateway.timeout", "Timed out", Now.AddMinutes(5));

        Assert.False(refund.TryStartAttempt(Now.AddMinutes(4)));
        Assert.True(refund.TryStartAttempt(Now.AddMinutes(5)));
        Assert.Equal(2, refund.AttemptCount);
    }

    [Fact]
    public void RejectedRefund_CannotBeRestarted()
    {
        Refund refund = Create();
        Assert.True(refund.TryStartAttempt(Now));
        refund.Reject("payment.invalid", "Original payment is invalid.");

        Assert.Equal(RefundStatus.Rejected, refund.Status);
        Assert.False(refund.TryStartAttempt(Now.AddHours(1)));
    }

    [Fact]
    public void ExpiredProcessingLease_ReturnsRefundToRetryQueue()
    {
        Refund refund = Create();
        Assert.True(refund.TryStartAttempt(Now));

        bool recovered = refund.RecoverExpiredAttempt(Now.AddMinutes(5), TimeSpan.FromMinutes(5),
            maxAttempts: 3, retryDelay: TimeSpan.FromMinutes(2));

        Assert.True(recovered);
        Assert.Equal(RefundStatus.RetryPending, refund.Status);
        Assert.Equal(Now.AddMinutes(7), refund.NextAttemptAtUtc);
        Assert.True(refund.TryStartAttempt(Now.AddMinutes(7)));
        Assert.Equal(2, refund.AttemptCount);
    }

    [Fact]
    public void ExpiredFinalProcessingLease_MarksRefundFailed()
    {
        Refund refund = Create();
        Assert.True(refund.TryStartAttempt(Now));

        bool recovered = refund.RecoverExpiredAttempt(Now.AddMinutes(5), TimeSpan.FromMinutes(5),
            maxAttempts: 1, retryDelay: TimeSpan.FromMinutes(2));

        Assert.True(recovered);
        Assert.Equal(RefundStatus.Failed, refund.Status);
    }

    private static Refund Create() => Refund.CreatePending(bookingId: 1, transactionId: 2,
        cancellationOperationId: 3, amount: 100_000m, reason: "Traveler request",
        initiatedBy: RefundInitiator.System, requestedAtUtc: Now);
}