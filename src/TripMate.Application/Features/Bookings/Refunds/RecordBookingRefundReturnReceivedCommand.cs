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
/// UC-42 steps 7-8 (SRS 3.8.4.3, spec v1.2): a Staff member reconciles the TripMate bank
/// statement and confirms the Tour Operator's full BR-139 return share has been credited. The
/// refund becomes eligible for the UC-67 automatic gateway sweep (<see cref="RefundProcessor"/>);
/// its amount is never recalculated here.
/// </summary>
public sealed record RecordBookingRefundReturnReceivedCommand(long RefundId)
    : IRequest<Result<RecordBookingRefundReturnReceivedResponse>>;

public sealed record RecordBookingRefundReturnReceivedResponse(long RefundId, string Status,
    DateTimeOffset ReturnReceivedAtUtc);

public sealed class RecordBookingRefundReturnReceivedCommandHandler(
    IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTimeProvider clock)
    : IRequestHandler<RecordBookingRefundReturnReceivedCommand, Result<RecordBookingRefundReturnReceivedResponse>>
{
    public async Task<Result<RecordBookingRefundReturnReceivedResponse>> Handle(
        RecordBookingRefundReturnReceivedCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long staffUserId || currentUser.Role != nameof(UserRole.Staff))
            return Result.Failure<RecordBookingRefundReturnReceivedResponse>(BookingCancellationErrorCodes.Unauthorized,
                "Staff access is required to record a refund return.");
        if (request.RefundId <= 0)
            return Result.Failure<RecordBookingRefundReturnReceivedResponse>(BookingCancellationErrorCodes.NotFound,
                "The refund could not be found.");

        return await dbContext.ExecuteInSerializableTransactionAsync(async token =>
        {
            Refund? refund = await dbContext.Refunds.SingleOrDefaultAsync(item => item.Id == request.RefundId, token);
            if (refund is null)
                return Result.Failure<RecordBookingRefundReturnReceivedResponse>(BookingCancellationErrorCodes.NotFound,
                    "The refund could not be found.");
            if (refund.Status != RefundStatus.ReturnReported)
                return Result.Failure<RecordBookingRefundReturnReceivedResponse>(RefundConfirmationErrorCodes.RequestNotPending,
                    "This refund has not reported a partner return awaiting receipt.");

            DateTimeOffset now = clock.UtcNow;
            RefundStatus statusBefore = refund.Status;
            refund.RecordReturnReceived(now);

            dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(staffUserId, "RefundReturnReceived", "Refund",
                refund.Id, now, AuditOutcome.Success,
                beforeData: JsonSerializer.Serialize(new { Status = statusBefore.ToString() }),
                afterData: JsonSerializer.Serialize(new { Status = refund.Status.ToString() })));
            await dbContext.SaveChangesAsync(token);

            return Result.Success(new RecordBookingRefundReturnReceivedResponse(refund.Id, refund.Status.ToString(),
                refund.ReturnReceivedAtUtc!.Value));
        }, cancellationToken);
    }
}