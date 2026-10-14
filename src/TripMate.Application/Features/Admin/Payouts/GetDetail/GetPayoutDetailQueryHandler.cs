using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.Payouts.GetList;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.Payouts.GetDetail;

public sealed class GetPayoutDetailQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<GetPayoutDetailQuery, Result<PayoutDetailDto>>
{
    // Masks every digit except the last 4; matches the Web spec's display format.
    private const string MaskPrefix = "XXXXXXXX";

    public async Task<Result<PayoutDetailDto>> Handle(
        GetPayoutDetailQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is null || currentUserService.Role != "Staff")
        {
            return Result.Failure<PayoutDetailDto>(
                PayoutErrorCodes.Forbidden,
                "You do not have permission to access this function.");
        }

        var payout = await dbContext.Payouts
            .AsNoTracking()
            .Where(candidate => candidate.Id == request.PayoutId)
            .Select(candidate => new DetailRow(
                candidate.Id,
                candidate.OperatorUserId,
                candidate.Operator.CompanyName,
                candidate.PeriodStart,
                candidate.PeriodEnd,
                candidate.GrossRevenue,
                candidate.CommissionAmount,
                candidate.NetAmount,
                candidate.RequestedAtUtc,
                candidate.Status,
                candidate.RejectionReason,
                candidate.BeneficiaryBankName,
                candidate.BeneficiaryAccountHolder,
                candidate.BeneficiaryAccountLast4))
            .FirstOrDefaultAsync(cancellationToken);

        if (payout is null)
        {
            // No audit row: BR-130 covers access to an existing record's beneficiary data, not a
            // lookup of a nonexistent one.
            return Result.Failure<PayoutDetailDto>(
                PayoutErrorCodes.NotFound,
                PayoutErrorCodes.NotFoundMessage);
        }

        // BR-130: fail-closed — the access audit row is persisted before the beneficiary data is
        // returned to the caller, same pattern as UC-59's GetActiveTripDetailsQueryHandler.
        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            currentUserService.UserId,
            AuditActionTypes.PayoutDetailsViewed,
            AuditEntityTypes.Payout,
            payout.PayoutId,
            dateTimeProvider.UtcNow,
            AuditOutcome.Success));
        await dbContext.SaveChangesAsync(cancellationToken);

        var commissionRate = payout.GrossRevenue == 0m
            ? 0m
            : payout.CommissionAmount / payout.GrossRevenue;

        var maskedAccountNumber = payout.BeneficiaryAccountLast4 is { } last4
            ? MaskPrefix + last4
            : null;

        return Result.Success(new PayoutDetailDto(
            payout.PayoutId.ToString(CultureInfo.InvariantCulture),
            Payout.PayoutCodePrefix + payout.PayoutId.ToString(CultureInfo.InvariantCulture),
            payout.Status,
            new PayoutSettlementPeriodDto(payout.PeriodStart, payout.PeriodEnd),
            new PayoutDetailOperatorDto(
                payout.OperatorUserId.ToString(CultureInfo.InvariantCulture),
                payout.CompanyName),
            payout.GrossRevenue,
            commissionRate,
            payout.CommissionAmount,
            payout.NetAmount,
            payout.RequestedAtUtc,
            // No Booking entity exists in this codebase yet (UC-46/booking generation is
            // unimplemented); this always returns empty until that dependency lands. See the
            // backend spec's "Still open" section — flagged, not silently decided.
            [],
            new PayoutBeneficiaryDto(
                payout.BeneficiaryBankName,
                payout.BeneficiaryAccountHolder,
                maskedAccountNumber),
            payout.RejectionReason));
    }

    private sealed record DetailRow(
        long PayoutId,
        long OperatorUserId,
        string CompanyName,
        DateOnly PeriodStart,
        DateOnly PeriodEnd,
        decimal GrossRevenue,
        decimal CommissionAmount,
        decimal NetAmount,
        DateTimeOffset? RequestedAtUtc,
        string Status,
        string? RejectionReason,
        string? BeneficiaryBankName,
        string? BeneficiaryAccountHolder,
        string? BeneficiaryAccountLast4);
}