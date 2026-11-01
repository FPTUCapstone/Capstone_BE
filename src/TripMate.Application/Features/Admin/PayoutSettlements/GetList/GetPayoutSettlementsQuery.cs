using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.Payouts.GetList;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Admin.PayoutSettlements.GetList;

/// <summary>UC-65 Administrator settlement queue. Same filters as the Staff review list, but the
/// status is limited to the settlement-execution scope; the role (not the client) fixes the scope.</summary>
public sealed record GetPayoutSettlementsQuery(
    string? Keyword = null,
    string? Status = Payout.StatusApprovedForSettlement,
    DateOnly? PeriodFrom = null,
    DateOnly? PeriodTo = null,
    int PageNumber = 1,
    int PageSize = GetPayoutsQuery.DefaultPageSize)
    : IRequest<Result<PayoutsResponseDto>>
{
    public static readonly string[] SettlementScopeStatuses =
    [
        Payout.StatusApprovedForSettlement,
        Payout.StatusSettlementProcessing,
        Payout.StatusSettled,
        Payout.StatusSettlementFailed,
    ];
}
