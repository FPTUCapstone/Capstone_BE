using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Payouts.GetSettlementMonths;

public sealed record GetOperatorSettlementMonthsQuery(int? Year)
    : IRequest<Result<IReadOnlyList<SettlementMonthDto>>>;

/// <summary>Informational only (UC-46 "available balance" model, 2026-10-23): the request action
/// is global (claims everything outstanding at once), not per-month, so this DTO has no
/// requestable/blockReason fields — only <see cref="Coverage"/> describing what state the month
/// is in.</summary>
public sealed record SettlementMonthDto(
    int Year,
    int Month,
    string MonthStatus,
    int EligibleBookingCount,
    decimal PaymentsReceived,
    decimal DiscountsGiven,
    decimal RefundedAmount,
    decimal NetSales,
    decimal CommissionAmount,
    decimal PayableNetAmount,
    string Coverage,
    string? PayoutCode);