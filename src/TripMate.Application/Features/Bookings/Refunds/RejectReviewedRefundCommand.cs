using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.Refunds;

/// <summary>
/// UC-42 SRS-alignment spec v1.2 §3.3 (<c>POST /admin/refunds/{id}/resolve</c>): an Administrator
/// closes a refund on the Escalated Refunds Monitor (<see cref="RefundStatus.Escalated"/>, BR-161)
/// or under reconciliation (<see cref="RefundStatus.Reconciliation"/>, UC-41 6.b1) without paying
/// it. Settling such a refund instead goes through <see cref="CompleteManualRefundCommand"/>.
/// </summary>
public sealed record RejectReviewedRefundCommand(long RefundId, string? Reason)
    : IRequest<Result<RejectReviewedRefundResponse>>;

public sealed record RejectReviewedRefundResponse(long RefundId, string Status);

public static class RefundResolutionActions
{
    public const string Complete = "Complete";
    public const string Reject = "Reject";
}

public sealed class RejectReviewedRefundCommandHandler(
    IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTimeProvider clock)
    : IRequestHandler<RejectReviewedRefundCommand, Result<RejectReviewedRefundResponse>>
{
    public const string RejectedErrorCode = "refund.rejected_by_administrator";
    public const string AuditAction = "RefundRejectedByAdministrator";
    public const string NotificationType = "BookingRefundRejected";

    public async Task<Result<RejectReviewedRefundResponse>> Handle(RejectReviewedRefundCommand request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long administratorId || currentUser.Role != nameof(UserRole.Administrator))
            return Result.Failure<RejectReviewedRefundResponse>(BookingCancellationErrorCodes.Unauthorized,
                "Administrator access is required to resolve a refund.");
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > Refund.ErrorMessageMaxLength)
            return Result.Failure<RejectReviewedRefundResponse>(BookingCancellationErrorCodes.ReasonRequired,
                "A reason is required to reject a refund.");

        string reason = request.Reason.Trim();
        return await dbContext.ExecuteInSerializableTransactionAsync(async token =>
        {
            Refund? refund = await dbContext.Refunds.Include(item => item.Booking)
                .SingleOrDefaultAsync(item => item.Id == request.RefundId, token);
            if (refund is null)
                return Result.Failure<RejectReviewedRefundResponse>(BookingCancellationErrorCodes.NotFound,
                    "The refund could not be found.");
            if (refund.Status is not (RefundStatus.Escalated or RefundStatus.Reconciliation))
                return Result.Failure<RejectReviewedRefundResponse>(RefundConfirmationErrorCodes.RequestNotPending,
                    "Only an escalated refund or one under reconciliation can be rejected.");

            DateTimeOffset now = clock.UtcNow;
            RefundStatus statusBefore = refund.Status;
            refund.Reject(RejectedErrorCode, reason);

            dbContext.Notifications.Add(new Notification
            {
                UserId = refund.Booking.TravelerUserId,
                Channel = NotificationChannel.Push,
                Type = NotificationType,
                Title = "Refund not processed",
                Body = $"The refund for booking {refund.Booking.BookingCode} was closed without payment.",
                RelatedEntityType = nameof(Refund),
                RelatedEntityId = refund.Id,
                Status = NotificationStatus.Pending,
                CreatedAtUtc = now,
            });
            dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(administratorId, AuditAction, nameof(Refund),
                refund.Id, now, AuditOutcome.Success, reason,
                beforeData: JsonSerializer.Serialize(new { Status = statusBefore.ToString() }),
                afterData: JsonSerializer.Serialize(new { Status = refund.Status.ToString() })));
            await dbContext.SaveChangesAsync(token);

            return Result.Success(new RejectReviewedRefundResponse(refund.Id, refund.Status.ToString()));
        }, cancellationToken);
    }
}