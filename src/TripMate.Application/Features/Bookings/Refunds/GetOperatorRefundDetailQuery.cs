using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.Refunds;

/// <summary>UC-42 read endpoint for FE (Q-FE2, spec v1.2 §3.3): a single refund's detail for the
/// Tour Operator that owns its booking's tour.</summary>
public sealed record GetOperatorRefundDetailQuery(long RefundId) : IRequest<Result<OperatorRefundDetailDto>>;

public sealed record OperatorRefundDetailDto(long RefundId, long BookingId, string BookingCode, decimal Amount,
    string Status, decimal? OperatorReturnShare, DateTimeOffset? OperatorConfirmedAtUtc,
    DateTimeOffset? ReturnDeadlineAtUtc, string? ReturnReference, DateTimeOffset? ReturnReportedAtUtc,
    DateTimeOffset? ReturnReceivedAtUtc, DateTimeOffset RequestedAtUtc);

public sealed class GetOperatorRefundDetailQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser)
    : IRequestHandler<GetOperatorRefundDetailQuery, Result<OperatorRefundDetailDto>>
{
    public async Task<Result<OperatorRefundDetailDto>> Handle(GetOperatorRefundDetailQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorUserId || currentUser.Role != nameof(UserRole.TourOperator))
            return Result.Failure<OperatorRefundDetailDto>(BookingCancellationErrorCodes.Unauthorized,
                "Tour Operator access is required to view this refund.");

        Refund? refund = await dbContext.Refunds.AsNoTracking()
            .Include(item => item.Booking).ThenInclude(booking => booking.TourSchedule).ThenInclude(schedule => schedule.Tour)
            .SingleOrDefaultAsync(item => item.Id == request.RefundId, cancellationToken);
        if (refund is null)
            return Result.Failure<OperatorRefundDetailDto>(BookingCancellationErrorCodes.NotFound,
                "The refund could not be found.");
        if (refund.Booking.TourSchedule.Tour.OperatorUserId != operatorUserId)
            return Result.Failure<OperatorRefundDetailDto>(RefundConfirmationErrorCodes.OperatorNotOwner,
                "Only the Tour Operator of this booking's tour may view this refund.");

        return Result.Success(new OperatorRefundDetailDto(refund.Id, refund.BookingId, refund.Booking.BookingCode,
            refund.Amount, refund.Status.ToString(), refund.OperatorReturnShare, refund.OperatorConfirmedAtUtc,
            refund.ReturnDeadlineAtUtc, refund.ReturnReference, refund.ReturnReportedAtUtc, refund.ReturnReceivedAtUtc,
            refund.RequestedAtUtc));
    }
}