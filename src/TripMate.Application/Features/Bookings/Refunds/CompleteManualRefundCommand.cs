using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.Refunds;

/// <summary>
/// Records an administrator-verified settlement for a provider which has no safe automatic
/// original-channel refund API. It never initiates a payout or invents beneficiary details.
/// </summary>
public sealed record CompleteManualRefundCommand(long RefundId, string? GatewayReference)
    : IRequest<Result<ManualRefundCompletionResponse>>;

public sealed record ManualRefundCompletionResponse(long RefundId, string Status, string GatewayReference,
    DateTimeOffset ProcessedAtUtc);

public sealed class CompleteManualRefundCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<CompleteManualRefundCommand, Result<ManualRefundCompletionResponse>>
{
    public async Task<Result<ManualRefundCompletionResponse>> Handle(CompleteManualRefundCommand request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long administratorId || currentUser.Role != nameof(UserRole.Administrator))
            return Result.Failure<ManualRefundCompletionResponse>(BookingCancellationErrorCodes.Unauthorized,
                "Administrator access is required to record a manual refund settlement.");
        if (request.RefundId <= 0 || string.IsNullOrWhiteSpace(request.GatewayReference) ||
            request.GatewayReference.Trim().Length > Refund.GatewayReferenceMaxLength)
            return Result.Failure<ManualRefundCompletionResponse>(BookingCancellationErrorCodes.InvalidState,
                "A valid gateway settlement reference is required.");

        return await dbContext.ExecuteInSerializableTransactionAsync(async token =>
        {
            Refund? refund = await dbContext.Refunds.SingleOrDefaultAsync(item => item.Id == request.RefundId, token);
            if (refund is null)
                return Result.Failure<ManualRefundCompletionResponse>(BookingCancellationErrorCodes.NotFound,
                    "The refund could not be found.");
            if (refund.Status != RefundStatus.ManualReview)
                return Result.Failure<ManualRefundCompletionResponse>(BookingCancellationErrorCodes.RefundInvalidState,
                    "Only a refund awaiting manual review can be completed.");

            Booking? booking = await dbContext.Bookings.Include(item => item.TourSchedule).ThenInclude(item => item.Tour)
                .SingleOrDefaultAsync(item => item.Id == refund.BookingId, token);
            PaymentTransaction? original = await dbContext.PaymentTransactions.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == refund.TransactionId, token);
            if (booking is null || original is null || original.Status != PaymentTransactionStatus.Success ||
                original.TransactionType != PaymentTransaction.TypePayment || original.Amount < refund.Amount)
                return Result.Failure<ManualRefundCompletionResponse>(BookingCancellationErrorCodes.RefundManualReviewRequired,
                    "The original payment is not valid for manual settlement.");

            DateTimeOffset now = clock.UtcNow;
            string reference = request.GatewayReference.Trim();
            PaymentTransaction settlement = PaymentTransaction.CreateRefund(booking.Id, refund.Id, original.Gateway,
                refund.Amount, now, original.Currency);
            settlement.MarkSuccess(reference, JsonSerializer.Serialize(new { source = "manual", administratorId }), now, 0m);
            refund.MarkProcessed(reference, now);
            decimal priorRefunded = await dbContext.Refunds.AsNoTracking()
                .Where(item => item.BookingId == booking.Id && item.Status == RefundStatus.Processed)
                .SumAsync(item => (decimal?)item.Amount, token) ?? 0m;
            booking.ApplyRefund(priorRefunded + refund.Amount);
            dbContext.PaymentTransactions.Add(settlement);
            AddCompletionNotifications(booking, refund, now);
            dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(administratorId, "ManualRefundCompleted", "Refund",
                refund.Id, now, AuditOutcome.Success,
                beforeData: JsonSerializer.Serialize(new { Status = RefundStatus.ManualReview.ToString() }),
                afterData: JsonSerializer.Serialize(new { Status = refund.Status.ToString(), GatewayReference = reference })));
            await dbContext.SaveChangesAsync(token);
            return Result.Success(new ManualRefundCompletionResponse(refund.Id, refund.Status.ToString(), reference,
                refund.ProcessedAtUtc!.Value));
        }, cancellationToken);
    }

    private void AddCompletionNotifications(Booking booking, Refund refund, DateTimeOffset now)
    {
        foreach (long userId in new[] { booking.TravelerUserId, booking.TourSchedule.Tour.OperatorUserId }.Distinct())
        {
            dbContext.Notifications.Add(new Notification
            {
                UserId = userId,
                Channel = NotificationChannel.Push,
                Type = "BookingRefundProcessed",
                Title = "Refund completed",
                Body = $"Refund for booking {booking.BookingCode} has been processed.",
                RelatedEntityType = "Refund",
                RelatedEntityId = refund.Id,
                Status = NotificationStatus.Pending,
                CreatedAtUtc = now,
            });
        }
    }
}