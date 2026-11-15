using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Application.Features.Bookings.Refunds;
using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.Cancel;

public sealed class CancelBookingCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    IBookingCancellationPersistenceErrorClassifier persistenceErrors)
    : IRequestHandler<CancelBookingCommand, Result<CancelBookingResponse>>
{
    private const int MaxDeadlockAttempts = 3;
    private const int ReturnDeadlineWorkingDays = 3;

    public async Task<Result<CancelBookingResponse>> Handle(
        CancelBookingCommand request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long actorUserId || actorUserId <= 0
            || !TryGetActor(currentUser.Role, out var actor))
        {
            return Result.Failure<CancelBookingResponse>(BookingCancellationErrorCodes.Unauthorized,
                "An authenticated Traveler or Tour Operator account is required.");
        }

        string requestHash = ComputeRequestHash(request.BookingId, actor, request.ReasonCategory, request.ReasonDetail,
            request.Confirmed);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
                {
                    var existingOperation = await FindOperationAsync(actorUserId, request.IdempotencyKey,
                        transactionCancellationToken);
                    if (existingOperation is not null)
                        return await ReplayOrMismatchAsync(existingOperation, request.BookingId, requestHash,
                            transactionCancellationToken);

                    var booking = await dbContext.Bookings
                        .Include(candidate => candidate.TourSchedule!)
                        .ThenInclude(schedule => schedule.Tour)
                        .Include(candidate => candidate.Tickets)
                        .SingleOrDefaultAsync(candidate => candidate.Id == request.BookingId, transactionCancellationToken);
                    if (booking is null || booking.TourSchedule is null)
                    {
                        return Result.Failure<CancelBookingResponse>(BookingCancellationErrorCodes.NotFound,
                            "The booking could not be found.");
                    }

                    if (!IsAuthorized(booking, actorUserId, actor))
                    {
                        return Result.Failure<CancelBookingResponse>(BookingCancellationErrorCodes.Forbidden,
                            "You are not permitted to cancel this booking.");
                    }

                    if (!BookingCancelEligibility.Evaluate(booking.Status,
                            booking.Tickets.Any(ticket => ticket.Status == TicketStatus.Used)).CanCancel)
                    {
                        return Result.Failure<CancelBookingResponse>(BookingCancellationErrorCodes.InvalidState,
                            "This booking can no longer be cancelled.");
                    }

                    if (actor == BookingCancellationActor.TourOperator
                        && (string.IsNullOrWhiteSpace(request.ReasonCategory)
                            || string.IsNullOrWhiteSpace(request.ReasonDetail)))
                    {
                        return Result.Failure<CancelBookingResponse>(BookingCancellationErrorCodes.ReasonRequired,
                            "Tour Operator cancellations require a reason category and detail.");
                    }

                    var nowUtc = clock.UtcNow;
                    PaymentTransaction? successfulPayment = await BookingCancellationPaymentVerifier
                        .FindSuccessfulPaymentAsync(dbContext, booking, transactionCancellationToken);
                    var decision = BookingCancellationRefundRules.Evaluate(booking,
                        actor == BookingCancellationActor.Traveler, nowUtc, request.Confirmed, successfulPayment);
                    if (decision.ErrorCode is not null)
                    {
                        return Result.Failure<CancelBookingResponse>(decision.ErrorCode, decision.ErrorMessage!);
                    }

                    // UC-41 6.a1/6.b1 (SRS 3.8.4.2): a missing or amount-mismatched payment never
                    // blocks the cancellation itself (BR-137 covers only the atomicity of the
                    // cancellation + capacity release + refund-request creation, not whether a
                    // refund exists at all) — it only changes what, if anything, gets created.
                    PaymentTransaction? anyPaymentIfMismatched = null;
                    if (decision.RefundAmount > 0m && successfulPayment is null)
                    {
                        anyPaymentIfMismatched = await BookingCancellationPaymentVerifier
                            .FindAnySuccessfulPaymentAsync(dbContext, booking, transactionCancellationToken);
                    }

                    decimal refundAmount = successfulPayment is null && anyPaymentIfMismatched is null
                        ? 0m // 6.a1: no verified payment at all — nothing to refund, MSG136.
                        : decision.RefundAmount;
                    // A zero-percent tier still creates no RefundRequest even when a matching
                    // payment exists — "refundAmount > 0" alone decides whether a Refund is
                    // created at all, below.
                    decimal operatorReturnShare = 0m;
                    DateTimeOffset? returnDeadlineAtUtc = null;
                    string refundStatus = refundAmount <= 0m
                        ? "NotRequired"
                        : successfulPayment is not null
                            ? nameof(RefundStatus.Pending)
                            : nameof(RefundStatus.Reconciliation); // 6.b1: amount mismatch, escalated.

                    if (refundAmount > 0m && successfulPayment is not null)
                    {
                        decimal transferredToOperator = await dbContext.PayoutSettlements.AsNoTracking()
                            .Where(settlement => settlement.BookingId == booking.Id &&
                                settlement.Status == PayoutSettlementStatus.Settled)
                            .SumAsync(settlement => (decimal?)settlement.PayoutAmount,
                                transactionCancellationToken) ?? 0m;
                        operatorReturnShare = RefundShareCalculator.CalculateOperatorReturnShare(refundAmount,
                            transferredToOperator, booking.TotalAmount);
                        returnDeadlineAtUtc = operatorReturnShare > 0m
                            ? WorkingDayCalculator.AddWorkingDays(nowUtc, ReturnDeadlineWorkingDays)
                            : null;
                        refundStatus = operatorReturnShare > 0m
                            ? nameof(RefundStatus.AwaitingPartnerReturn)
                            : nameof(RefundStatus.Pending);
                    }

                    var operation = BookingCancellationOperation.Create(booking.Id, actorUserId, actor,
                        request.IdempotencyKey, requestHash, refundAmount, refundStatus, nowUtc);
                    dbContext.BookingCancellationOperations.Add(operation);
                    await dbContext.SaveChangesAsync(transactionCancellationToken);

                    var beforeState = new { status = booking.Status.ToString(), paymentStatus = booking.PaymentStatus.ToString() };
                    string? reason = ComposeReason(request.ReasonCategory, request.ReasonDetail);
                    bool releaseCouponUsage = booking.Status == BookingStatus.PendingPayment &&
                                              booking.PaymentStatus == PaymentStatus.Unpaid;
                    booking.Cancel(nowUtc, reason);
                    booking.TourSchedule.ReleaseCapacity(booking.Quantity);
                    if (releaseCouponUsage)
                        await BookingVoucherUsage.ReleaseAsync(dbContext, booking, transactionCancellationToken);
                    foreach (var ticket in booking.Tickets) ticket.Cancel();
                    await dbContext.RevokeActiveTicketQrCodesAsync(booking.Id, nowUtc, transactionCancellationToken);

                    var initiator = actor == BookingCancellationActor.TourOperator
                        ? RefundInitiator.TourOperator : RefundInitiator.System;
                    Refund? createdRefund = null;
                    if (refundAmount > 0m && successfulPayment is not null)
                    {
                        createdRefund = Refund.CreateAutomatically(booking.Id, successfulPayment.Id, operation.Id,
                            refundAmount, reason, initiator, operatorReturnShare, returnDeadlineAtUtc, nowUtc);
                        dbContext.Refunds.Add(createdRefund);
                    }
                    else if (refundAmount > 0m && anyPaymentIfMismatched is not null)
                    {
                        createdRefund = Refund.CreateReconciliation(booking.Id, anyPaymentIfMismatched.Id,
                            operation.Id, refundAmount, reason, initiator, nowUtc);
                        dbContext.Refunds.Add(createdRefund);
                        await NotifyAdministratorsOfReconciliationAsync(booking.Id, nowUtc, transactionCancellationToken);
                    }

                    dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(actorUserId, AuditActionTypes.BookingCancel,
                        AuditEntityTypes.Booking, booking.Id, nowUtc, AuditOutcome.Success, reason,
                        beforeData: JsonSerializer.Serialize(beforeState),
                        afterData: JsonSerializer.Serialize(new
                        {
                            status = BookingStatus.Cancelled.ToString(), refundAmount, refundStatus,
                            operatorReturnShare, returnDeadlineAtUtc, actor,
                        })));
                    await dbContext.SaveChangesAsync(transactionCancellationToken);
                    return Result.Success(ToResponse(operation, createdRefund?.Id));
                }, cancellationToken);
            }
            catch (DbUpdateException exception) when (persistenceErrors.IsIdempotencyKeyConflict(exception))
            {
                dbContext.ClearTrackedEntities();
                var operation = await FindOperationAsync(actorUserId, request.IdempotencyKey, cancellationToken);
                if (operation is not null)
                    return await ReplayOrMismatchAsync(operation, request.BookingId, requestHash, cancellationToken);
                throw;
            }
            catch (Exception exception) when (dbContext.IsDeadlock(exception))
            {
                dbContext.ClearTrackedEntities();
                if (attempt >= MaxDeadlockAttempts)
                {
                    return Result.Failure<CancelBookingResponse>(
                        BookingCancellationErrorCodes.ConcurrentRetryExhausted,
                        "Cancellation is busy because another request is updating this booking. Please try again.");
                }

                // Avoid immediately recreating the same lock cycle. The delay is bounded,
                // deterministic and cancellation-aware: 25ms, then 50ms.
                await Task.Delay(TimeSpan.FromMilliseconds(25 * (1 << (attempt - 1))), cancellationToken);
            }
        }
    }

    private static bool TryGetActor(string? role, out BookingCancellationActor actor)
    {
        actor = role == nameof(UserRole.TourOperator)
            ? BookingCancellationActor.TourOperator
            : BookingCancellationActor.Traveler;
        return role is nameof(UserRole.Traveler) or nameof(UserRole.TourOperator);
    }

    private static bool IsAuthorized(Booking booking, long actorUserId, BookingCancellationActor actor) =>
        actor == BookingCancellationActor.Traveler
            ? booking.TravelerUserId == actorUserId
            : booking.TourSchedule!.Tour.OperatorUserId == actorUserId;

    private Task<BookingCancellationOperation?> FindOperationAsync(long actorUserId, Guid idempotencyKey,
        CancellationToken cancellationToken) => dbContext.BookingCancellationOperations.AsNoTracking()
        .SingleOrDefaultAsync(operation => operation.ActorUserId == actorUserId
            && operation.IdempotencyKey == idempotencyKey, cancellationToken);

    private async Task<Result<CancelBookingResponse>> ReplayOrMismatchAsync(BookingCancellationOperation operation,
        long bookingId, string requestHash, CancellationToken cancellationToken)
    {
        if (operation.BookingId != bookingId || operation.RequestHash != requestHash)
        {
            return Result.Failure<CancelBookingResponse>(BookingCancellationErrorCodes.IdempotencyPayloadMismatch,
                "This idempotency key has already been used with a different cancellation request.");
        }

        long? refundId = await dbContext.Refunds.AsNoTracking()
            .Where(refund => refund.CancellationOperationId == operation.Id)
            .Select(refund => (long?)refund.Id)
            .SingleOrDefaultAsync(cancellationToken);
        return Result.Success(ToResponse(operation, refundId));
    }

    private static string? ComposeReason(string? category, string? detail) =>
        string.IsNullOrWhiteSpace(category)
            ? string.IsNullOrWhiteSpace(detail) ? null : detail.Trim()
            : string.IsNullOrWhiteSpace(detail) ? category.Trim() : $"{category.Trim()}: {detail.Trim()}";

    private static string ComputeRequestHash(long bookingId, BookingCancellationActor actor, string? category, string? detail,
        bool confirmed)
    {
        string canonical = $"{bookingId}\n{actor}\n{category?.Trim() ?? string.Empty}\n{detail?.Trim() ?? string.Empty}"
            + (confirmed ? "\nconfirmed" : string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static CancelBookingResponse ToResponse(BookingCancellationOperation operation, long? refundId) =>
        new(operation.BookingId, nameof(BookingStatus.Cancelled), operation.RefundAmount,
            operation.RefundStatus, operation.CancelledAtUtc, refundId);

    /// <summary>UC-41 6.b1 (SRS 3.8.4.2, MSG136): "the system marks the refund request for
    /// reconciliation by the Administrator" — notifies every active Administrator.</summary>
    private async Task NotifyAdministratorsOfReconciliationAsync(long bookingId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var administratorIds = await dbContext.Users.AsNoTracking()
            .Where(user => user.Role == UserRole.Administrator && user.Status == AccountStatus.Active)
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);

        foreach (var administratorId in administratorIds)
        {
            dbContext.Notifications.Add(new Notification
            {
                UserId = administratorId,
                Channel = NotificationChannel.Push,
                Type = "BookingRefundReconciliationRequired",
                Title = "Refund needs reconciliation",
                Body = $"Booking {bookingId}: the verified payment amount does not match the refund amount. Review required.",
                RelatedEntityType = Notification.BookingRelatedEntityType,
                RelatedEntityId = bookingId,
                Status = NotificationStatus.Pending,
                CreatedAtUtc = nowUtc,
            });
        }
    }
}
