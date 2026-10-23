using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Payouts.GetAvailableBalance;

/// <summary>
/// UC-46 "available balance" summary (decided 2026-10-23): the aggregate across every closed,
/// eligible, not-yet-paid settlement month, backing the single "Request Payout" action. The
/// actual request handler recomputes this independently inside its own transaction — this query
/// is read-only and for display/enablement only, never trusted by the mutating command.
/// </summary>
public sealed record GetOperatorAvailableBalanceQuery : IRequest<Result<AvailableBalanceDto>>;

public sealed record AvailableBalanceDto(
    int PeriodCount,
    decimal PaymentsReceived,
    decimal DiscountsGiven,
    decimal RefundedAmount,
    decimal NetSales,
    decimal CommissionAmount,
    decimal AdjustmentsOutstanding,
    decimal PayableNetAmount,
    bool Requestable,
    string? BlockReason,
    string? ActivePayoutCode);