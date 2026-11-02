using MediatR;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.Payouts.Common;

namespace TripMate.Application.Features.Admin.Payouts.GetList;

public sealed class GetPayoutsQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService)
    : IRequestHandler<GetPayoutsQuery, Result<PayoutsResponseDto>>
{
    public async Task<Result<PayoutsResponseDto>> Handle(
        GetPayoutsQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is null || currentUserService.Role != "Staff")
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
            || !GetPayoutsQuery.StaffVisibleStatuses.Contains(request.Status))
        {
            return Result.Failure<PayoutsResponseDto>(
                PayoutErrorCodes.InvalidStatus,
                PayoutErrorCodes.InvalidStatusMessage);
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