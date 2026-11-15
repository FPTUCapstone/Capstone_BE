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
/// UC-42 step 5 (SRS 3.8.4.3, spec v1.2): the Tour Operator reports having transferred its BR-139
/// return share by bank transfer. A missing reference is rejected (MSG172) before any database
/// access, since it never depends on the refund's current state.
/// </summary>
public sealed record ReportBookingRefundReturnCommand(long RefundId, string? ReturnReference)
    : IRequest<Result<ReportBookingRefundReturnResponse>>;

public sealed record ReportBookingRefundReturnResponse(long RefundId, string Status, string ReturnReference,
    DateTimeOffset ReturnReportedAtUtc);

public sealed class ReportBookingRefundReturnCommandHandler(
    IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTimeProvider clock)
    : IRequestHandler<ReportBookingRefundReturnCommand, Result<ReportBookingRefundReturnResponse>>
{
    public async Task<Result<ReportBookingRefundReturnResponse>> Handle(ReportBookingRefundReturnCommand request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorUserId || currentUser.Role != nameof(UserRole.TourOperator))
            return Result.Failure<ReportBookingRefundReturnResponse>(BookingCancellationErrorCodes.Unauthorized,
                "Tour Operator access is required to report a refund return.");
        if (request.RefundId <= 0)
            return Result.Failure<ReportBookingRefundReturnResponse>(BookingCancellationErrorCodes.NotFound,
                "The refund could not be found.");
        if (string.IsNullOrWhiteSpace(request.ReturnReference) ||
            request.ReturnReference.Trim().Length > Refund.ReturnReferenceMaxLength)
        {
            return Result.Failure<ReportBookingRefundReturnResponse>(RefundConfirmationErrorCodes.ReturnReferenceRequired,
                "A bank-transfer reference is required to report a return.");
        }

        string reference = request.ReturnReference.Trim();
        return await dbContext.ExecuteInSerializableTransactionAsync(async token =>
        {
            Refund? refund = await dbContext.Refunds.SingleOrDefaultAsync(item => item.Id == request.RefundId, token);
            if (refund is null)
                return Result.Failure<ReportBookingRefundReturnResponse>(BookingCancellationErrorCodes.NotFound,
                    "The refund could not be found.");

            Booking? booking = await dbContext.Bookings.Include(item => item.TourSchedule).ThenInclude(item => item.Tour)
                .SingleOrDefaultAsync(item => item.Id == refund.BookingId, token);
            if (booking is null)
                return Result.Failure<ReportBookingRefundReturnResponse>(BookingCancellationErrorCodes.NotFound,
                    "The refund could not be found.");
            if (booking.TourSchedule.Tour.OperatorUserId != operatorUserId)
                return Result.Failure<ReportBookingRefundReturnResponse>(RefundConfirmationErrorCodes.OperatorNotOwner,
                    "Only the Tour Operator of this booking's tour may report this refund's return.");
            if (refund.Status != RefundStatus.AwaitingPartnerReturn)
                return Result.Failure<ReportBookingRefundReturnResponse>(RefundConfirmationErrorCodes.RequestNotPending,
                    "This refund is not awaiting a partner return report.");

            DateTimeOffset now = clock.UtcNow;
            RefundStatus statusBefore = refund.Status;
            refund.ReportReturn(reference, now);

            dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(operatorUserId, "RefundReturnReported", "Refund",
                refund.Id, now, AuditOutcome.Success,
                beforeData: JsonSerializer.Serialize(new { Status = statusBefore.ToString() }),
                afterData: JsonSerializer.Serialize(new { Status = refund.Status.ToString(), ReturnReference = reference })));
            await dbContext.SaveChangesAsync(token);

            return Result.Success(new ReportBookingRefundReturnResponse(refund.Id, refund.Status.ToString(), reference,
                refund.ReturnReportedAtUtc!.Value));
        }, cancellationToken);
    }
}