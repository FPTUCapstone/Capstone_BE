using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Payouts.DailySettlement.Reads;

/// <summary>"Payout Settlement History" screen (SRS v11 Table 4.1 row 91, PD-13): a Tour
/// Operator's own settlements, per booking. Read-only, scoped to the signed-in operator.</summary>
public sealed record GetOperatorPayoutSettlementHistoryQuery(int PageNumber = 1, int PageSize = 20)
    : IRequest<Result<PayoutSettlementsResponseDto>>;

public sealed class GetOperatorPayoutSettlementHistoryQueryHandler(
    IApplicationDbContext dbContext, ICurrentUserService currentUserService)
    : IRequestHandler<GetOperatorPayoutSettlementHistoryQuery, Result<PayoutSettlementsResponseDto>>
{
    public async Task<Result<PayoutSettlementsResponseDto>> Handle(
        GetOperatorPayoutSettlementHistoryQuery request, CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is null || currentUserService.Role != nameof(UserRole.TourOperator))
            return Result.Failure<PayoutSettlementsResponseDto>(DailyPayoutErrorCodes.Forbidden, DailyPayoutErrorCodes.ForbiddenMessage);

        var operatorId = currentUserService.UserId.Value;
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var pageNumber = Math.Max(request.PageNumber, 1);

        var query = dbContext.PayoutSettlements.AsNoTracking()
            .Where(s => s.TourOperatorUserId == operatorId);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(s => s.RunDate).ThenBy(s => s.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new PayoutSettlementListItemDto(
                s.Id, s.BookingId, s.TourOperatorUserId, s.RunDate, s.Kind, s.PayoutAmount, s.Status, s.TransferReference))
            .ToListAsync(cancellationToken);

        return Result.Success(new PayoutSettlementsResponseDto(pageNumber, pageSize, totalCount, items));
    }
}
