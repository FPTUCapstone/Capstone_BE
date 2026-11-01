using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.Payouts.GetDetail;

namespace TripMate.Application.Features.Admin.PayoutSettlements.GetDetail;

public sealed record GetPayoutSettlementDetailQuery(long PayoutId) : IRequest<Result<PayoutSettlementDetailDto>>;

/// <summary>Safe summary of what the payout worker has done. No gateway reference, no raw gateway
/// response, no credentials.</summary>
public sealed record PayoutSettlementOutcomeDto(
    string GatewayState,
    string? FailureCode,
    DateTimeOffset? SubmittedAtUtc,
    bool ReconciliationRequired);

public sealed record PayoutSettlementDetailDto(
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
    DateTimeOffset? ReviewedAtUtc,
    string? ReviewedBy,
    DateTimeOffset? SettlementAuthorizedAtUtc,
    string? SettlementAuthorizedBy,
    PayoutBeneficiaryDto Beneficiary,
    bool BankBinResolved,
    PayoutSettlementOutcomeDto? Outcome);
