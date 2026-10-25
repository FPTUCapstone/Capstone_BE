using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Payouts.RequestPayout;

/// <summary>
/// "Available balance" request (UC-46, decided 2026-10-23): takes no parameters. The backend
/// selects every closed, eligible, not-yet-paid settlement month for the operator and folds them
/// into one payout. The operator never picks a month or edits an amount.
/// </summary>
public sealed record RequestPayoutCommand : IRequest<Result<PayoutSummaryDto>>;

public sealed record PayoutMonthBreakdownDto(
    int Year,
    int Month,
    int EligibleBookingCount,
    decimal PaymentsReceived,
    decimal DiscountsGiven,
    decimal RefundedAmount,
    decimal CommissionAmount,
    decimal PayableNetAmount);

public sealed record PayoutSummaryDto(
    string PayoutCode,
    string Status,
    int PeriodCount,
    string PeriodStart,
    string PeriodEnd,
    decimal PaymentsReceived,
    decimal DiscountsGiven,
    decimal RefundedAmount,
    decimal NetSales,
    decimal CommissionAmount,
    decimal AdjustmentsApplied,
    decimal PayableNetAmount,
    DateTimeOffset RequestedAtUtc,
    string BankName,
    string AccountHolderName,
    string MaskedAccountNumber,
    IReadOnlyList<PayoutMonthBreakdownDto> Months);