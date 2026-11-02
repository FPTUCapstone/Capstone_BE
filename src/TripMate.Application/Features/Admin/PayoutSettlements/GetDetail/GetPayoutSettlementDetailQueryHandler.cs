using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.Payouts.Common;
using TripMate.Application.Features.Admin.Payouts.GetDetail;
using TripMate.Application.Features.Admin.PayoutSettlements.Common;
using TripMate.Application.Features.Admin.PayoutSettlements.GetList;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.PayoutSettlements.GetDetail;

public sealed class GetPayoutSettlementDetailQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<GetPayoutSettlementDetailQuery, Result<PayoutSettlementDetailDto>>
{
    private const string NotQueuedState = "NotQueued";

    public async Task<Result<PayoutSettlementDetailDto>> Handle(
        GetPayoutSettlementDetailQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is null || currentUserService.Role != nameof(UserRole.Administrator))
        {
            return Result.Failure<PayoutSettlementDetailDto>(
                PayoutErrorCodes.Forbidden,
                "You do not have permission to access this function.");
        }

        var payout = await dbContext.Payouts
            .AsNoTracking()
            .Where(candidate => candidate.Id == request.PayoutId
                && GetPayoutSettlementsQuery.SettlementScopeStatuses.Contains(candidate.Status))
            .Select(candidate => new DetailRow(
                candidate.Id,
                candidate.OperatorUserId,
                candidate.Operator.CompanyName,
                candidate.PayoutCode,
                candidate.PeriodStart,
                candidate.PeriodEnd,
                candidate.GrossRevenue,
                candidate.CommissionAmount,
                candidate.NetAmount,
                candidate.RequestedAtUtc,
                candidate.ReviewedBy,
                candidate.ReviewedAtUtc,
                candidate.SettlementAuthorizedBy,
                candidate.SettlementAuthorizedAtUtc,
                candidate.Status,
                candidate.GatewayFailureReason,
                candidate.BankName,
                candidate.BankBin,
                candidate.AccountHolderName,
                candidate.AccountNumberMasked))
            .FirstOrDefaultAsync(cancellationToken);

        if (payout is null)
        {
            // A payout outside the settlement scope is indistinguishable from a missing one here,
            // and no audit row is written: BR-130 covers access to a record this actor may open.
            return Result.Failure<PayoutSettlementDetailDto>(
                PayoutErrorCodes.NotFound,
                PayoutErrorCodes.NotFoundMessage);
        }

        var catalogue = await dbContext.Banks
            .AsNoTracking()
            .Select(bank => new BankCatalogueEntry(bank.BankBin, bank.ShortName, bank.OfficialName))
            .ToListAsync(cancellationToken);
        var bankBinResolved = BankBinResolver.Resolve(payout.BankBin, payout.BankName, catalogue).IsSuccess;

        PayoutSettlementOutcomeDto? outcome = null;
        if (payout.Status != Payout.StatusApprovedForSettlement)
        {
            var workItem = await dbContext.PayoutSettlementWorkItems
                .AsNoTracking()
                .Where(item => item.PayoutId == payout.PayoutId)
                .Select(item => new { item.State, item.SubmittedAtUtc })
                .FirstOrDefaultAsync(cancellationToken);
            outcome = new PayoutSettlementOutcomeDto(
                workItem?.State ?? NotQueuedState,
                payout.GatewayFailureReason,
                workItem?.SubmittedAtUtc,
                workItem?.State == PayoutSettlementWorkItem.StateManualReconciliation);
        }

        // BR-130: fail-closed. The access audit row is persisted before beneficiary data is returned.
        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            currentUserService.UserId,
            AuditActionTypes.PayoutSettlementDetailsViewed,
            AuditEntityTypes.Payout,
            payout.PayoutId,
            dateTimeProvider.UtcNow,
            AuditOutcome.Success));
        await dbContext.SaveChangesAsync(cancellationToken);

        var commissionRate = payout.GrossRevenue == 0m ? 0m : payout.CommissionAmount / payout.GrossRevenue;

        return Result.Success(new PayoutSettlementDetailDto(
            payout.PayoutId.ToString(CultureInfo.InvariantCulture),
            payout.PayoutCode,
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
            payout.ReviewedAtUtc,
            payout.ReviewedBy?.ToString(CultureInfo.InvariantCulture),
            payout.SettlementAuthorizedAtUtc,
            payout.SettlementAuthorizedBy?.ToString(CultureInfo.InvariantCulture),
            new PayoutBeneficiaryDto(payout.BankName, payout.AccountHolderName, payout.AccountNumberMasked),
            bankBinResolved,
            outcome));
    }

    private sealed record DetailRow(
        long PayoutId,
        long OperatorUserId,
        string CompanyName,
        string PayoutCode,
        DateOnly PeriodStart,
        DateOnly PeriodEnd,
        decimal GrossRevenue,
        decimal CommissionAmount,
        decimal NetAmount,
        DateTimeOffset? RequestedAtUtc,
        long? ReviewedBy,
        DateTimeOffset? ReviewedAtUtc,
        long? SettlementAuthorizedBy,
        DateTimeOffset? SettlementAuthorizedAtUtc,
        string Status,
        string? GatewayFailureReason,
        string? BankName,
        string? BankBin,
        string? AccountHolderName,
        string? AccountNumberMasked);
}