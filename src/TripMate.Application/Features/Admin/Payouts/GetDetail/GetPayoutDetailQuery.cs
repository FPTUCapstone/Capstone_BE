using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Admin.Payouts.GetDetail;

public sealed record GetPayoutDetailQuery(long PayoutId) : IRequest<Result<PayoutDetailDto>>;

public sealed record PayoutDetailOperatorDto(string OperatorId, string CompanyName);

public sealed record PayoutSettlementPeriodDto(DateOnly StartDate, DateOnly EndDate);

public sealed record PayoutBeneficiaryDto(
    string? BankName,
    string? AccountHolder,
    string? MaskedAccountNumber);

public sealed record PayoutContributingBookingDto(
    string BookingId,
    string BookingCode,
    string TourName,
    DateTimeOffset CompletedAtUtc,
    decimal PaidAmount,
    decimal RefundedAmount,
    decimal NetAmount);

public sealed record PayoutDetailDto(
    string PayoutId,
    string PayoutCode,
    string Status,
    PayoutSettlementPeriodDto SettlementPeriod,
    PayoutDetailOperatorDto TourOperator,
    decimal GrossRevenue,
    decimal CommissionRate,
    decimal CommissionAmount,
    decimal NetPayoutAmount,
    DateTimeOffset? RequestedAtUtc,
    IReadOnlyList<PayoutContributingBookingDto> ContributingBookings,
    PayoutBeneficiaryDto Beneficiary,
    string? RejectionReason);
