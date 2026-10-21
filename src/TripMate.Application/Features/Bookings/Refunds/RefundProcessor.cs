using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.Refunds;

/// <summary>
/// UC-42 automatic refund worker. The cancellation flow has already made the refund amount
/// immutable; this processor only verifies, submits, records and retries it.
/// </summary>
public sealed class RefundProcessor(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock,
    IRefundPaymentGatewayResolver gatewayResolver,
    RefundProcessingOptions options)
{
    public async Task<RefundSweepResult> ProcessDueRefundsAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await RecoverExpiredAttemptsAsync(now, cancellationToken);
        List<long> ids = await dbContext.Refunds.AsNoTracking()
            .Where(refund => (refund.Status == RefundStatus.Pending || refund.Status == RefundStatus.RetryPending) &&
                             refund.NextAttemptAtUtc <= now)
            .OrderBy(refund => refund.NextAttemptAtUtc).ThenBy(refund => refund.Id)
            .Select(refund => refund.Id).Take(options.BatchSize).ToListAsync(cancellationToken);

        var completed = 0;
        var deferred = 0;
        var failures = new List<RefundSweepFailure>();
        foreach (long refundId in ids)
        {
            try
            {
                RefundClaim? claim = await dbContext.ExecuteInSerializableTransactionAsync(
                    token => ClaimAsync(refundId, token), cancellationToken);
                if (claim is null) continue;

                RefundSubmissionResult submission;
                IPaymentGateway? gateway = gatewayResolver.Resolve(claim.OriginalGateway);
                if (gateway is null)
                {
                    submission = new RefundSubmissionResult(false,
                        FailureCode: "payment.refund.manual_review_required",
                        FailureMessage: "The original payment provider is not available for automatic refunds.",
                        IsRetryable: false,
                        RequiresManualReview: true);
                }
                else
                {
                    try
                    {
                        submission = await gateway.SubmitRefundAsync(new RefundSubmissionRequest(claim.RefundId,
                            claim.BookingId, claim.Amount, claim.Currency, claim.OriginalGatewayReference, claim.Reason,
                            $"tripmate-refund-{claim.RefundId}"), cancellationToken);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or TimeoutException)
                    {
                        submission = new RefundSubmissionResult(false,
                            FailureCode: "payment.refund.gateway_unavailable",
                            FailureMessage: "The payment gateway did not respond.", IsRetryable: true);
                    }
                }

                bool succeeded = await dbContext.ExecuteInSerializableTransactionAsync(
                    token => RecordOutcomeAsync(claim, submission, gateway?.Gateway, token), cancellationToken);
                if (succeeded) completed++; else deferred++;
            }
            catch (Exception exception)
            {
                dbContext.ClearTrackedEntities();
                failures.Add(new RefundSweepFailure(refundId, exception.GetType().Name));
            }
        }

        return new RefundSweepResult(completed, deferred, failures);
    }

    private async Task RecoverExpiredAttemptsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        DateTimeOffset expiredBefore = now.Subtract(options.ProcessingLeaseDuration);
        List<long> ids = await dbContext.Refunds.AsNoTracking()
            .Where(refund => refund.Status == RefundStatus.Processing &&
                             refund.LastAttemptAtUtc != null && refund.LastAttemptAtUtc <= expiredBefore)
            .OrderBy(refund => refund.LastAttemptAtUtc).ThenBy(refund => refund.Id)
            .Select(refund => refund.Id).Take(options.BatchSize).ToListAsync(cancellationToken);

        foreach (long refundId in ids)
        {
            await dbContext.ExecuteInSerializableTransactionAsync(async token =>
            {
                Refund? refund = await dbContext.Refunds.SingleOrDefaultAsync(item => item.Id == refundId, token);
                if (refund is not null && refund.RecoverExpiredAttempt(now, options.ProcessingLeaseDuration,
                        options.MaxAttempts, options.RetryDelay))
                {
                    await dbContext.SaveChangesAsync(token);
                    return true;
                }

                return false;
            }, cancellationToken);
        }
    }

    private async Task<RefundClaim?> ClaimAsync(long refundId, CancellationToken cancellationToken)
    {
        Refund? refund = await dbContext.Refunds.SingleOrDefaultAsync(candidate => candidate.Id == refundId,
            cancellationToken);
        if (refund is null || !refund.TryStartAttempt(clock.UtcNow)) return null;

        PaymentTransaction? original = await dbContext.PaymentTransactions.AsNoTracking()
            .SingleOrDefaultAsync(transaction => transaction.Id == refund.TransactionId, cancellationToken);
        if (original is null || original.TransactionType != PaymentTransaction.TypePayment ||
            original.Status != PaymentTransactionStatus.Success || original.Amount < refund.Amount)
        {
            refund.Reject("payment.refund.original_payment_invalid",
                "The original payment is not verified for the requested refund amount.");
            await dbContext.SaveChangesAsync(cancellationToken);
            return null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new RefundClaim(refund.Id, refund.BookingId, refund.Amount, original.Currency,
            original.Gateway, original.GatewayTransactionRef, refund.Reason);
    }

    private async Task<bool> RecordOutcomeAsync(RefundClaim claim, RefundSubmissionResult submission,
        PaymentGateway? gateway,
        CancellationToken cancellationToken)
    {
        Refund? refund = await dbContext.Refunds.SingleOrDefaultAsync(candidate => candidate.Id == claim.RefundId,
            cancellationToken);
        if (refund is null || refund.Status != RefundStatus.Processing) return false;

        Booking? booking = await dbContext.Bookings.Include(item => item.TourSchedule).ThenInclude(item => item.Tour)
            .SingleOrDefaultAsync(item => item.Id == refund.BookingId, cancellationToken);
        if (booking is null) throw new InvalidOperationException("Refund booking no longer exists.");

        var now = clock.UtcNow;
        if (submission.IsAccepted)
        {
            if (gateway is null)
                throw new InvalidOperationException("An accepted refund must identify its original payment gateway.");

            var transaction = PaymentTransaction.CreateRefund(booking.Id, refund.Id, gateway.Value, refund.Amount, now,
                claim.Currency);
            dbContext.PaymentTransactions.Add(transaction);
            string reference = submission.GatewayReference ?? $"REFUND-{refund.Id}-{refund.AttemptCount}";
            transaction.MarkSuccess(reference, JsonSerializer.Serialize(submission), now, 0m);
            refund.MarkProcessed(reference, now);
            // The just-completed refund is tracked but not committed yet, so add its immutable
            // amount explicitly rather than relying on a database query to see it.
            decimal priorRefunded = await dbContext.Refunds.AsNoTracking()
                .Where(item => item.BookingId == booking.Id && item.Status == RefundStatus.Processed)
                .SumAsync(item => (decimal?)item.Amount, cancellationToken) ?? 0m;
            booking.ApplyRefund(priorRefunded + refund.Amount);
            AddNotifications(booking, refund, now);
        }
        else if (submission.RequiresManualReview)
        {
            refund.RequireManualReview(
                submission.FailureCode ?? "payment.refund.manual_review_required",
                submission.FailureMessage);
            await AddManualReviewNotificationsAsync(booking, refund, now, cancellationToken);
        }
        else
        {
            if (gateway is null)
                throw new InvalidOperationException("A submitted refund failure must identify its original payment gateway.");

            var transaction = PaymentTransaction.CreateRefund(booking.Id, refund.Id, gateway.Value, refund.Amount, now,
                claim.Currency);
            dbContext.PaymentTransactions.Add(transaction);
            transaction.MarkFailed(JsonSerializer.Serialize(submission), now);
            string code = submission.FailureCode ?? "payment.refund.gateway_rejected";
            if (submission.IsRetryable && refund.AttemptCount < options.MaxAttempts)
                refund.ScheduleRetry(code, submission.FailureMessage, now.Add(options.RetryDelay));
            else
                refund.MarkFailed(code, submission.FailureMessage);
        }

        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(null, "RefundGatewaySubmission", "Refund",
            refund.Id, now, submission.IsAccepted ? AuditOutcome.Success : AuditOutcome.Failure,
            submission.FailureMessage, afterData: JsonSerializer.Serialize(new { refund.Status, refund.AttemptCount })));
        await dbContext.SaveChangesAsync(cancellationToken);
        return submission.IsAccepted;
    }

    private void AddNotifications(Booking booking, Refund refund, DateTimeOffset now)
    {
        dbContext.Notifications.Add(new Notification
        {
            UserId = booking.TravelerUserId,
            Channel = NotificationChannel.Push,
            Type = "BookingRefundProcessed",
            Title = "Refund completed",
            Body = $"Your refund of {refund.Amount:N0} VND has been processed.",
            RelatedEntityType = "Refund",
            RelatedEntityId = refund.Id,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = now
        });
        dbContext.Notifications.Add(new Notification
        {
            UserId = booking.TourSchedule.Tour.OperatorUserId,
            Channel = NotificationChannel.Push,
            Type = "BookingRefundProcessed",
            Title = "Refund completed",
            Body = $"Refund for booking {booking.BookingCode} has been processed.",
            RelatedEntityType = "Refund",
            RelatedEntityId = refund.Id,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = now
        });
    }

    private async Task AddManualReviewNotificationsAsync(Booking booking, Refund refund, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string body = "Your refund is awaiting manual payment-provider processing.";
        dbContext.Notifications.Add(new Notification
        {
            UserId = booking.TravelerUserId,
            Channel = NotificationChannel.Push,
            Type = "BookingRefundManualReview",
            Title = "Refund is under review",
            Body = body,
            RelatedEntityType = "Refund",
            RelatedEntityId = refund.Id,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = now,
        });
        dbContext.Notifications.Add(new Notification
        {
            UserId = booking.TourSchedule.Tour.OperatorUserId,
            Channel = NotificationChannel.Push,
            Type = "BookingRefundManualReview",
            Title = "Refund is under review",
            Body = $"Refund for booking {booking.BookingCode} requires manual payment-provider processing.",
            RelatedEntityType = "Refund",
            RelatedEntityId = refund.Id,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = now,
        });

        List<long> administratorIds = await dbContext.Users.AsNoTracking()
            .Where(user => user.Role == UserRole.Administrator && user.Status == AccountStatus.Active)
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);
        foreach (long administratorId in administratorIds)
        {
            dbContext.Notifications.Add(new Notification
            {
                UserId = administratorId,
                Channel = NotificationChannel.Push,
                Type = "BookingRefundManualReviewRequired",
                Title = "Manual refund required",
                Body = $"Refund {refund.Id} for booking {booking.BookingCode} requires PayOS settlement.",
                RelatedEntityType = "Refund",
                RelatedEntityId = refund.Id,
                Status = NotificationStatus.Pending,
                CreatedAtUtc = now,
            });
        }
    }

    private sealed record RefundClaim(long RefundId, long BookingId, decimal Amount, string Currency,
        PaymentGateway OriginalGateway, string? OriginalGatewayReference, string? Reason);
}

public sealed record RefundSweepResult(int ProcessedCount, int DeferredCount, IReadOnlyList<RefundSweepFailure> Failures);
public sealed record RefundSweepFailure(long RefundId, string ExceptionType);