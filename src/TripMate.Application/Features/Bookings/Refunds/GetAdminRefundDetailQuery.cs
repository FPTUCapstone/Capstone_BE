using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.Refunds;

/// <summary>UC-42 read endpoint for FE (Q-FE2, spec v1.2 §3.3): a single refund's detail for
/// Staff/Administrator, regardless of which tour's booking it belongs to.</summary>
public sealed record GetAdminRefundDetailQuery(long RefundId) : IRequest<Result<AdminRefundDetailDto>>;

public sealed record AdminRefundDetailDto(long RefundId, long BookingId, string BookingCode, decimal Amount,
    string Status, decimal? OperatorReturnShare, DateTimeOffset? OperatorConfirmedAtUtc,
    DateTimeOffset? ReturnDeadlineAtUtc, string? ReturnReference, DateTimeOffset? ReturnReportedAtUtc,
    DateTimeOffset? ReturnReceivedAtUtc, string? LastErrorCode, string? LastErrorMessage, DateTimeOffset RequestedAtUtc);

public sealed class GetAdminRefundDetailQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser)
    : IRequestHandler<GetAdminRefundDetailQuery, Result<AdminRefundDetailDto>>
{
    public async Task<Result<AdminRefundDetailDto>> Handle(GetAdminRefundDetailQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUser.Role != nameof(UserRole.Staff) && currentUser.Role != nameof(UserRole.Administrator))
            return Result.Failure<AdminRefundDetailDto>(BookingCancellationErrorCodes.Unauthorized,
                "Staff or Administrator access is required to view this refund.");

        Refund? refund = await dbContext.Refunds.AsNoTracking().Include(item => item.Booking)
            .SingleOrDefaultAsync(item => item.Id == request.RefundId, cancellationToken);
        if (refund is null)
            return Result.Failure<AdminRefundDetailDto>(BookingCancellationErrorCodes.NotFound,
                "The refund could not be found.");

        return Result.Success(new AdminRefundDetailDto(refund.Id, refund.BookingId, refund.Booking.BookingCode,
            refund.Amount, refund.Status.ToString(), refund.OperatorReturnShare, refund.OperatorConfirmedAtUtc,
            refund.ReturnDeadlineAtUtc, refund.ReturnReference, refund.ReturnReportedAtUtc, refund.ReturnReceivedAtUtc,
            refund.LastErrorCode, refund.LastErrorMessage, refund.RequestedAtUtc));
    }
}