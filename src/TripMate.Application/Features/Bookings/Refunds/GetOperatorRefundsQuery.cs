using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.Refunds;

/// <summary>UC-42 read endpoint for FE (Q-FE2, spec v1.2 §3.3): the signed-in Tour Operator's own
/// "Confirm Booking Refund" list — only refunds of bookings on tours it owns.</summary>
public sealed record GetOperatorRefundsQuery(string? Status = null, int PageNumber = 1, int PageSize = 20)
    : IRequest<Result<OperatorRefundsResponseDto>>;

public sealed record OperatorRefundListItemDto(long RefundId, long BookingId, string BookingCode, decimal Amount,
    string Status, decimal? OperatorReturnShare, DateTimeOffset? ReturnDeadlineAtUtc, DateTimeOffset RequestedAtUtc);

public sealed record OperatorRefundsResponseDto(
    int PageNumber, int PageSize, int TotalCount, IReadOnlyList<OperatorRefundListItemDto> Items);

public sealed class GetOperatorRefundsQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser)
    : IRequestHandler<GetOperatorRefundsQuery, Result<OperatorRefundsResponseDto>>
{
    public async Task<Result<OperatorRefundsResponseDto>> Handle(GetOperatorRefundsQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorUserId || currentUser.Role != nameof(UserRole.TourOperator))
            return Result.Failure<OperatorRefundsResponseDto>(BookingCancellationErrorCodes.Unauthorized,
                "Tour Operator access is required to list refunds.");

        int pageSize = Math.Clamp(request.PageSize, 1, 100);
        int pageNumber = Math.Max(request.PageNumber, 1);

        var query = dbContext.Refunds.AsNoTracking()
            .Where(refund => refund.Booking.TourSchedule.Tour.OperatorUserId == operatorUserId);
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            if (!Enum.TryParse<RefundStatus>(request.Status, out RefundStatus parsedStatus))
                return Result.Success(new OperatorRefundsResponseDto(pageNumber, pageSize, 0, []));
            query = query.Where(refund => refund.Status == parsedStatus);
        }

        int totalCount = await query.CountAsync(cancellationToken);
        List<OperatorRefundListItemDto> items = await query
            .OrderByDescending(refund => refund.RequestedAtUtc).ThenBy(refund => refund.Id)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize)
            .Select(refund => new OperatorRefundListItemDto(refund.Id, refund.BookingId, refund.Booking.BookingCode,
                refund.Amount, refund.Status.ToString(), refund.OperatorReturnShare, refund.ReturnDeadlineAtUtc,
                refund.RequestedAtUtc))
            .ToListAsync(cancellationToken);

        return Result.Success(new OperatorRefundsResponseDto(pageNumber, pageSize, totalCount, items));
    }
}