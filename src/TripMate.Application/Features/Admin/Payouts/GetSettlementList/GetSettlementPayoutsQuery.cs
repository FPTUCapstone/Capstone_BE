using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.Payouts.GetList;

namespace TripMate.Application.Features.Admin.Payouts.GetSettlementList;

/// <summary>Administrator settlement queue. The status is fixed server-side.</summary>
public sealed record GetSettlementPayoutsQuery(
    string? Keyword = null,
    DateOnly? PeriodFrom = null,
    DateOnly? PeriodTo = null,
    int PageNumber = 1,
    int PageSize = GetPayoutsQuery.DefaultPageSize)
    : IRequest<Result<PayoutsResponseDto>>
{
    public const int MaximumKeywordLength = GetPayoutsQuery.MaximumKeywordLength;
    public const int MaximumPageSize = GetPayoutsQuery.MaximumPageSize;
}
