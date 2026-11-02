using System.Globalization;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.Payouts.Common;
using TripMate.Application.Features.Admin.PayoutSettlements.Common;
using TripMate.Application.Features.Operator.Payouts.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.PayoutSettlements.Authorize;

/// <summary>
/// UC-65: the Administrator authorizes settlement. This handler only changes state: it moves the
/// payout to SettlementProcessing and queues exactly one durable work item, in a single save. It
/// never calls the payment gateway; the payout-settlement worker does that afterwards.
/// </summary>
public sealed class AuthorizePayoutSettlementCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider,
    IPayoutPersistenceErrorClassifier persistenceErrors)
    : IRequestHandler<AuthorizePayoutSettlementCommand, Result<AuthorizePayoutSettlementResponseDto>>
{
    public async Task<Result<AuthorizePayoutSettlementResponseDto>> Handle(
        AuthorizePayoutSettlementCommand request,
        CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is not long administratorId
            || currentUserService.Role != nameof(UserRole.Administrator))
        {
            return Result.Failure<AuthorizePayoutSettlementResponseDto>(
                PayoutErrorCodes.Forbidden,
                "You do not have permission to access this function.");
        }

        // Tracked: the status change and the work item are saved together below.
        var payout = await dbContext.Payouts
            .FirstOrDefaultAsync(candidate => candidate.Id == request.PayoutId, cancellationToken);
        if (payout is null)
        {
            return Result.Failure<AuthorizePayoutSettlementResponseDto>(
                PayoutErrorCodes.NotFound,
                PayoutErrorCodes.NotFoundMessage);
        }

        if (payout.Status != Payout.StatusApprovedForSettlement)
        {
            return NotApproved(payout.Status);
        }

        if (string.IsNullOrWhiteSpace(payout.BankName)
            || string.IsNullOrWhiteSpace(payout.AccountHolderName)
            || string.IsNullOrWhiteSpace(payout.AccountNumberProtected))
        {
            return Result.Failure<AuthorizePayoutSettlementResponseDto>(
                PayoutSettlementErrorCodes.BeneficiaryIncomplete,
                PayoutSettlementErrorCodes.BeneficiaryIncompleteMessage);
        }

        // PayOS takes an integer amount of VND. The snapshot is paid exactly as stored; it is never
        // rounded or recalculated here.
        if (payout.NetAmount <= 0m || payout.NetAmount != decimal.Truncate(payout.NetAmount))
        {
            return Result.Failure<AuthorizePayoutSettlementResponseDto>(
                PayoutSettlementErrorCodes.AmountInvalid,
                PayoutSettlementErrorCodes.AmountInvalidMessage);
        }

        var catalogue = await dbContext.Banks
            .AsNoTracking()
            .Select(bank => new BankCatalogueEntry(bank.BankBin, bank.ShortName, bank.OfficialName))
            .ToListAsync(cancellationToken);
        var bankBin = BankBinResolver.Resolve(payout.BankBin, payout.BankName, catalogue);
        if (bankBin.IsFailure)
        {
            return Result.Failure<AuthorizePayoutSettlementResponseDto>(bankBin.ErrorCode!, bankBin.ErrorMessage!);
        }

        var now = dateTimeProvider.UtcNow;
        if (!payout.AuthorizeSettlement(administratorId, now, bankBin.Value))
        {
            return NotApproved(payout.Status);
        }

        dbContext.PayoutSettlementWorkItems.Add(PayoutSettlementWorkItem.Create(payout.Id, now));
        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            administratorId,
            AuditActionTypes.PayoutSettlementAuthorized,
            AuditEntityTypes.Payout,
            payout.Id,
            now,
            AuditOutcome.Success,
            beforeData: JsonSerializer.Serialize(new { Status = Payout.StatusApprovedForSettlement }),
            afterData: JsonSerializer.Serialize(new { Status = Payout.StatusSettlementProcessing })));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request moved this ApprovedForSettlement row first (the Status concurrency
            // token). Report what is committed now, not this process's in-memory entity.
            return await ConflictFromCommittedStatusAsync(request.PayoutId, cancellationToken);
        }
        catch (DbUpdateException exception) when (persistenceErrors.IsSettlementWorkItemConflict(exception))
        {
            return await ConflictFromCommittedStatusAsync(request.PayoutId, cancellationToken);
        }

        return Result.Success(new AuthorizePayoutSettlementResponseDto(
            payout.Id.ToString(CultureInfo.InvariantCulture),
            payout.PayoutCode,
            payout.Status,
            now));
    }

    private async Task<Result<AuthorizePayoutSettlementResponseDto>> ConflictFromCommittedStatusAsync(
        long payoutId,
        CancellationToken cancellationToken)
    {
        var currentStatus = await dbContext.Payouts
            .AsNoTracking()
            .Where(candidate => candidate.Id == payoutId)
            .Select(candidate => candidate.Status)
            .FirstOrDefaultAsync(cancellationToken);
        return NotApproved(currentStatus);
    }

    private static Result<AuthorizePayoutSettlementResponseDto> NotApproved(string? currentStatus) =>
        Result.Failure<AuthorizePayoutSettlementResponseDto>(
            PayoutSettlementErrorCodes.NotApprovedForSettlement,
            PayoutSettlementErrorCodes.NotApprovedForSettlementMessage,
            new Dictionary<string, object?> { ["currentStatus"] = currentStatus });
}