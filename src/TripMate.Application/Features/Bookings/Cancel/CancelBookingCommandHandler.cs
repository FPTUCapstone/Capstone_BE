using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Common;
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

        string requestHash = ComputeRequestHash(request.BookingId, actor, request.ReasonCategory, request.ReasonDetail);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
                {
                    var existingOperation = await FindOperationAsync(actorUserId, request.IdempotencyKey,
                        transactionCancellationToken);
                    if (existingOperation is not null) return ReplayOrMismatch(existingOperation, request.BookingId, requestHash);

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

                    if (booking.Status is not (BookingStatus.PendingPayment or BookingStatus.Confirmed)
                        || booking.Tickets.Any(ticket => ticket.Status == TicketStatus.Used))
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
                    decimal refundAmount = DetermineRefundAmount(booking, actor, nowUtc, out var failure);
                    if (failure is not null) return failure;
                    PaymentTransaction? successfulPayment = await BookingCancellationPaymentVerifier
                        .FindSuccessfulPaymentAsync(dbContext, booking, transactionCancellationToken);
                    if (refundAmount > 0m && successfulPayment is null)
                    {
                        return Result.Failure<CancelBookingResponse>(BookingCancellationErrorCodes.PaymentNotRefundable,
                            "This booking has no verified payment eligible for a refund.");
                    }

                    var operation = BookingCancellationOperation.Create(booking.Id, actorUserId, actor,
                        request.IdempotencyKey, requestHash, refundAmount, nowUtc);
                    dbContext.BookingCancellationOperations.Add(operation);
                    await dbContext.SaveChangesAsync(transactionCancellationToken);

                    var beforeState = new { status = booking.Status.ToString(), paymentStatus = booking.PaymentStatus.ToString() };
                    string? reason = ComposeReason(request.ReasonCategory, request.ReasonDetail);
                    booking.Cancel(nowUtc, reason);
                    booking.TourSchedule.ReleaseCapacity(booking.Quantity);
                    foreach (var ticket in booking.Tickets) ticket.Cancel();
                    await dbContext.RevokeActiveTicketQrCodesAsync(booking.Id, nowUtc, transactionCancellationToken);

                    if (refundAmount > 0m)
                    {
                        dbContext.Refunds.Add(Refund.CreatePending(booking.Id, successfulPayment!.Id, operation.Id,
                            refundAmount, reason, actor == BookingCancellationActor.TourOperator
                                ? RefundInitiator.TourOperator : RefundInitiator.System, nowUtc));
                    }

                    dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(actorUserId, AuditActionTypes.BookingCancel,
                        AuditEntityTypes.Booking, booking.Id, nowUtc, AuditOutcome.Success, reason,
                        beforeData: JsonSerializer.Serialize(beforeState),
                        afterData: JsonSerializer.Serialize(new { status = BookingStatus.Cancelled.ToString(), refundAmount, actor })));
                    await dbContext.SaveChangesAsync(transactionCancellationToken);
                    return Result.Success(ToResponse(operation));
                }, cancellationToken);
            }
            catch (DbUpdateException exception) when (persistenceErrors.IsIdempotencyKeyConflict(exception))
            {
                dbContext.ClearTrackedEntities();
                var operation = await FindOperationAsync(actorUserId, request.IdempotencyKey, cancellationToken);
                if (operation is not null) return ReplayOrMismatch(operation, request.BookingId, requestHash);
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

    private static Result<CancelBookingResponse> ReplayOrMismatch(BookingCancellationOperation operation,
        long bookingId, string requestHash) => operation.BookingId == bookingId && operation.RequestHash == requestHash
        ? Result.Success(ToResponse(operation))
        : Result.Failure<CancelBookingResponse>(BookingCancellationErrorCodes.IdempotencyPayloadMismatch,
            "This idempotency key has already been used with a different cancellation request.");

    private static decimal DetermineRefundAmount(Booking booking, BookingCancellationActor actor,
        DateTimeOffset nowUtc, out Result<CancelBookingResponse>? failure)
    {
        failure = null;
        if (booking.Status == BookingStatus.PendingPayment) return 0m;
        if (actor == BookingCancellationActor.TourOperator) return booking.TotalAmount;
        if (!TravelerCancellationPolicy.TryParse(booking.CancellationPolicySnapshot, out var policy))
        {
            failure = Result.Failure<CancelBookingResponse>(BookingCancellationErrorCodes.PolicyUnavailable,
                "The booking cancellation policy is unavailable.");
            return 0m;
        }

        decimal? percent = policy!.RefundPercent(nowUtc, booking.TourSchedule!.StartAtUtc);
        if (percent is null)
        {
            failure = Result.Failure<CancelBookingResponse>(BookingCancellationErrorCodes.WindowClosed,
                "Bookings cannot be cancelled less than 24 hours before departure.");
            return 0m;
        }

        return decimal.Round(booking.TotalAmount * percent.Value / 100m, 2, MidpointRounding.AwayFromZero);
    }

    private static string? ComposeReason(string? category, string? detail) =>
        string.IsNullOrWhiteSpace(category)
            ? string.IsNullOrWhiteSpace(detail) ? null : detail.Trim()
            : string.IsNullOrWhiteSpace(detail) ? category.Trim() : $"{category.Trim()}: {detail.Trim()}";

    private static string ComputeRequestHash(long bookingId, BookingCancellationActor actor, string? category, string? detail)
    {
        string canonical = $"{bookingId}\n{actor}\n{category?.Trim() ?? string.Empty}\n{detail?.Trim() ?? string.Empty}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static CancelBookingResponse ToResponse(BookingCancellationOperation operation) =>
        new(operation.BookingId, nameof(BookingStatus.Cancelled), operation.RefundAmount,
            operation.RefundAmount > 0m ? nameof(RefundStatus.Pending) : "NotRequired",
            operation.CancelledAtUtc);
}