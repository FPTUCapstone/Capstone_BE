using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.PostTripComplaints.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.PostTripComplaints.Respond;

public sealed record RespondToPostTripComplaintCommand(long ComplaintId, Guid IdempotencyKey,
    PostTripComplaintResponseAction Action, decimal? ProposedRefundAmount, string? ProposalReason, string? RejectionReason)
    : IRequest<Result<RespondToPostTripComplaintResponse>>;

public sealed record RespondToPostTripComplaintResponse(long ComplaintId, string Status,
    PostTripComplaintResponseAction? ResponseAction, decimal RefundableAmount, decimal? ProposedRefundAmount,
    DateTimeOffset? TravelerResponseDeadlineUtc, long? RefundId, bool IsReplay);

public sealed class RespondToPostTripComplaintCommandHandler(
    IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTimeProvider clock)
    : IRequestHandler<RespondToPostTripComplaintCommand, Result<RespondToPostTripComplaintResponse>>
{
    private const int TravelerResponseDays = 3;

    public async Task<Result<RespondToPostTripComplaintResponse>> Handle(
        RespondToPostTripComplaintCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorUserId || operatorUserId <= 0 ||
            currentUser.Role != nameof(UserRole.TourOperator))
        {
            return Result.Failure<RespondToPostTripComplaintResponse>(OperatorPostTripComplaintErrorCodes.Unauthorized,
                "An active Tour Operator account is required.");
        }
        if (request.ComplaintId <= 0 || request.IdempotencyKey == Guid.Empty)
            return Result.Failure<RespondToPostTripComplaintResponse>(OperatorPostTripComplaintErrorCodes.NotFound,
                "The complaint could not be found.");

        string fingerprint = Fingerprint(request);
        return await dbContext.ExecuteInSerializableTransactionAsync(async token =>
        {
            bool activeOperator = await dbContext.Users.AsNoTracking().AnyAsync(user => user.Id == operatorUserId &&
                user.Role == UserRole.TourOperator && user.Status == AccountStatus.Active, token);
            if (!activeOperator)
                return Result.Failure<RespondToPostTripComplaintResponse>(OperatorPostTripComplaintErrorCodes.Unauthorized,
                    "An active Tour Operator account is required.");

            PostTripComplaint? complaint = await dbContext.PostTripComplaints.SingleOrDefaultAsync(
                item => item.Id == request.ComplaintId, token);
            if (complaint is null)
                return Result.Failure<RespondToPostTripComplaintResponse>(OperatorPostTripComplaintErrorCodes.NotFound,
                    "The complaint could not be found.");

            Booking? booking = await dbContext.Bookings.Include(item => item.TourSchedule).ThenInclude(item => item.Tour)
                .SingleOrDefaultAsync(item => item.Id == complaint.BookingId, token);
            if (booking?.TourSchedule is null || booking.TourSchedule.Tour.OperatorUserId != operatorUserId)
            {
                dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(operatorUserId, "PostTripComplaintResponseDenied",
                    Notification.PostTripComplaintRelatedEntityType, complaint.Id, clock.UtcNow, AuditOutcome.Failure));
                await dbContext.SaveChangesAsync(token);
                return Result.Failure<RespondToPostTripComplaintResponse>(OperatorPostTripComplaintErrorCodes.Forbidden,
                    "You are not permitted to respond to this complaint.");
            }

            PostTripComplaintResponseOperation? prior = await dbContext.PostTripComplaintResponseOperations
                .AsNoTracking().SingleOrDefaultAsync(operation => operation.OperatorUserId == operatorUserId &&
                    operation.IdempotencyKey == request.IdempotencyKey, token);
            if (prior is not null)
            {
                if (prior.ComplaintId != complaint.Id || prior.PayloadFingerprint != fingerprint)
                    return Result.Failure<RespondToPostTripComplaintResponse>(OperatorPostTripComplaintErrorCodes.IdempotencyMismatch,
                        "This idempotency key has already been used with a different response.");
                return Result.Success(ToResponse(complaint, true));
            }

            DateTimeOffset now = clock.UtcNow;
            if (complaint.ExpireOperatorResponse(now) || complaint.DeclineExpiredOperatorReconsideration(now))
            {
                dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(operatorUserId, "PostTripComplaintResponseExpired",
                    Notification.PostTripComplaintRelatedEntityType, complaint.Id, now, AuditOutcome.Failure));
                await dbContext.SaveChangesAsync(token);
                return Closed();
            }
            if (complaint.Status is not (PostTripComplaintStatus.AwaitingOperatorResponse or PostTripComplaintStatus.AwaitingOperatorReconsideration))
            {
                dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(operatorUserId, "PostTripComplaintResponseDenied",
                    Notification.PostTripComplaintRelatedEntityType, complaint.Id, now, AuditOutcome.Failure));
                await dbContext.SaveChangesAsync(token);
                return Closed();
            }

            Result validation = Validate(request, complaint);
            if (validation.IsFailure)
            {
                dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(operatorUserId, "PostTripComplaintResponseRejected",
                    Notification.PostTripComplaintRelatedEntityType, complaint.Id, now, AuditOutcome.Failure, validation.ErrorMessage));
                await dbContext.SaveChangesAsync(token);
                return Result.Failure<RespondToPostTripComplaintResponse>(validation.ErrorCode!, validation.ErrorMessage!);
            }

            bool isReconsideration = complaint.Status == PostTripComplaintStatus.AwaitingOperatorReconsideration;
            int negotiationRound = complaint.PartialRefundProposalRound;
            if (request.Action == PostTripComplaintResponseAction.AcceptFullRefund)
            {
                PaymentTransaction? payment = await dbContext.PaymentTransactions.AsNoTracking()
                    .Where(item => item.BookingId == booking.Id && item.TransactionType == PaymentTransaction.TypePayment &&
                        item.Status == PaymentTransactionStatus.Success && item.Amount >= complaint.RefundableAmount)
                    .OrderByDescending(item => item.SucceededAtUtc).FirstOrDefaultAsync(token);
                if (payment is null || complaint.RefundableAmount <= 0m)
                    return Result.Failure<RespondToPostTripComplaintResponse>(OperatorPostTripComplaintErrorCodes.RefundUnavailable,
                        "A verified refundable payment is required.");

                var refund = Refund.CreateForPostTripComplaint(booking.Id, payment.Id, complaint.Id,
                    complaint.RefundableAmount, now);
                dbContext.Refunds.Add(refund);
                await dbContext.SaveChangesAsync(token);
                complaint.AcceptFullRefund(operatorUserId, refund.Id, now);
                if (isReconsideration)
                {
                    dbContext.PostTripComplaintNegotiationEvents.Add(PostTripComplaintNegotiationEvent.Create(complaint.Id,
                        negotiationRound, operatorUserId, PostTripComplaintNegotiationActor.TourOperator,
                        PostTripComplaintNegotiationEventType.Accepted, complaint.RefundableAmount, null, now));
                }
            }
            else if (request.Action == PostTripComplaintResponseAction.ProposePartialRefund)
            {
                complaint.ProposePartialRefund(operatorUserId, request.ProposedRefundAmount!.Value, request.ProposalReason!, now,
                    now.AddDays(TravelerResponseDays));
                dbContext.PostTripComplaintNegotiationEvents.Add(PostTripComplaintNegotiationEvent.Create(complaint.Id,
                    complaint.PartialRefundProposalRound, operatorUserId, PostTripComplaintNegotiationActor.TourOperator,
                    PostTripComplaintNegotiationEventType.PartialRefundOffer, request.ProposedRefundAmount,
                    request.ProposalReason, now));
            }
            else
            {
                complaint.Reject(operatorUserId, request.RejectionReason!, now);
                if (isReconsideration)
                {
                    dbContext.PostTripComplaintNegotiationEvents.Add(PostTripComplaintNegotiationEvent.Create(complaint.Id,
                        negotiationRound, operatorUserId, PostTripComplaintNegotiationActor.TourOperator,
                        PostTripComplaintNegotiationEventType.Rejected, null, request.RejectionReason, now));
                }
            }

            dbContext.PostTripComplaintResponseOperations.Add(PostTripComplaintResponseOperation.Create(operatorUserId,
                request.IdempotencyKey, fingerprint, complaint.Id, now));
            dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(operatorUserId, "PostTripComplaintResponded",
                Notification.PostTripComplaintRelatedEntityType, complaint.Id, now, AuditOutcome.Success,
                afterData: JsonSerializer.Serialize(new { complaint.Status, complaint.ResponseAction,
                    complaint.ProposedRefundAmount, complaint.RefundId })));
            dbContext.Notifications.Add(new Notification
            {
                UserId = complaint.TravelerUserId,
                Channel = NotificationChannel.Push,
                Type = "PostTripComplaintResponded",
                Title = "Complaint updated",
                Body = "The Tour Operator has responded to your complaint.",
                RelatedEntityType = Notification.PostTripComplaintRelatedEntityType,
                RelatedEntityId = complaint.Id,
                Status = NotificationStatus.Pending,
                CreatedAtUtc = now,
            });
            await dbContext.SaveChangesAsync(token);
            return Result.Success(ToResponse(complaint, false));
        }, cancellationToken);
    }

    private static Result Validate(RespondToPostTripComplaintCommand request, PostTripComplaint complaint) =>
        request.Action switch
        {
            PostTripComplaintResponseAction.ProposePartialRefund when request.ProposedRefundAmount is not > 0m ||
                request.ProposedRefundAmount > complaint.RefundableAmount =>
                Result.Failure(OperatorPostTripComplaintErrorCodes.AmountExceedsRefundable,
                    "The proposed refund must be greater than zero and no greater than the refundable amount."),
            PostTripComplaintResponseAction.ProposePartialRefund when string.IsNullOrWhiteSpace(request.ProposalReason) ||
                request.ProposalReason.Trim().Length > PostTripComplaint.PartialRefundProposalReasonMaxLength =>
                Result.Failure(OperatorPostTripComplaintErrorCodes.ProposalReasonRequired,
                    "A reason is required for a partial-refund proposal."),
            PostTripComplaintResponseAction.Reject when string.IsNullOrWhiteSpace(request.RejectionReason) ||
                request.RejectionReason.Trim().Length > PostTripComplaint.RejectionReasonMaxLength =>
                Result.Failure(OperatorPostTripComplaintErrorCodes.RejectionReasonRequired,
                    "A rejection reason is required."),
            _ => Result.Success(),
        };

    private static Result<RespondToPostTripComplaintResponse> Closed() =>
        Result.Failure<RespondToPostTripComplaintResponse>(OperatorPostTripComplaintErrorCodes.ResponseClosed,
            "This complaint is no longer open for an operator response.");

    private static RespondToPostTripComplaintResponse ToResponse(PostTripComplaint complaint, bool replay) =>
        new(complaint.Id, complaint.Status.ToString(), complaint.ResponseAction, complaint.RefundableAmount,
            complaint.ProposedRefundAmount, complaint.TravelerResponseDeadlineUtc, complaint.RefundId, replay);

    private static string Fingerprint(RespondToPostTripComplaintCommand request)
    {
        string canonical = $"{request.ComplaintId}\n{request.Action}\n{request.ProposedRefundAmount?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty}\n{request.ProposalReason?.Trim() ?? string.Empty}\n{request.RejectionReason?.Trim() ?? string.Empty}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
