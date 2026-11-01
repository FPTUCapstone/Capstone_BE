using MediatR;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.Payouts.Common;
using TripMate.Application.Features.Admin.Payouts.GetList;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.PayoutSettlements.GetList;

public sealed class GetPayoutSettlementsQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService)
    : IRequestHandler<GetPayoutSettlementsQuery, Result<PayoutsResponseDto>>
{
    public async Task<Result<PayoutsResponseDto>> Handle(
        GetPayoutSettlementsQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is null || currentUserService.Role != nameof(UserRole.Administrator))
        {
            return Result.Failure<PayoutsResponseDto>(
                PayoutErrorCodes.Forbidden,
                "You do not have permission to access this function.");
        }

        if (request.PeriodFrom.HasValue && request.PeriodTo.HasValue
            && request.PeriodFrom.Value > request.PeriodTo.Value)
        {
            return Result.Failure<PayoutsResponseDto>(
                PayoutErrorCodes.InvalidPeriodRange,
                PayoutErrorCodes.InvalidPeriodRangeMessage);
        }

        if (string.IsNullOrWhiteSpace(request.Status)
            || !GetPayoutSettlementsQuery.SettlementScopeStatuses.Contains(request.Status))
        {
            return Result.Failure<PayoutsResponseDto>(
                PayoutErrorCodes.InvalidStatus,
                "The requested payout status is not available for settlement.");
        }

        var response = await PayoutListReader.ReadAsync(
            dbContext,
            new PayoutListFilter(
                request.Keyword,
                request.Status,
                request.PeriodFrom,
                request.PeriodTo,
                request.PageNumber,
                request.PageSize),
            cancellationToken);

        return Result.Success(response);
    }
}
