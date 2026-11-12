using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Payouts.DailySettlement.Admin;

/// <summary>UC-73 [Retry Transfer] (SRS 3.10.6, spec v7): Administrator resends a failed batch
/// once, with the same amount (idempotent reference). Performed synchronously — the result
/// (Settled, or back to SettlementFailed with MSG176) is reflected in the response at once; see
/// <see cref="DailyPayoutRetrySweepProcessor.ManualRetryAsync"/>.</summary>
public sealed record RetryFailedPayoutBatchCommand(long BatchId) : IRequest<Result<DailyPayoutBatchActionResponseDto>>;

public sealed record DailyPayoutBatchActionResponseDto(long BatchId, string Status, DateTimeOffset UpdatedAtUtc);

public sealed class RetryFailedPayoutBatchCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider,
    DailyPayoutRetrySweepProcessor retryProcessor)
    : IRequestHandler<RetryFailedPayoutBatchCommand, Result<DailyPayoutBatchActionResponseDto>>
{
    public async Task<Result<DailyPayoutBatchActionResponseDto>> Handle(
        RetryFailedPayoutBatchCommand request, CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is null || currentUserService.Role != nameof(UserRole.Administrator))
            return Result.Failure<DailyPayoutBatchActionResponseDto>(DailyPayoutErrorCodes.Forbidden, DailyPayoutErrorCodes.ForbiddenMessage);

        var batch = await dbContext.PayoutBatches
            .Include(b => b.Settlements)
            .FirstOrDefaultAsync(b => b.Id == request.BatchId, cancellationToken);
        if (batch is null)
            return Result.Failure<DailyPayoutBatchActionResponseDto>(DailyPayoutErrorCodes.BatchNotFound, DailyPayoutErrorCodes.BatchNotFoundMessage);

        if (batch.Status != PayoutSettlementStatus.SettlementFailed)
            return Result.Failure<DailyPayoutBatchActionResponseDto>(DailyPayoutErrorCodes.NotFailed, DailyPayoutErrorCodes.NotFailedMessage);

        var now = dateTimeProvider.UtcNow;
        var outcome = await retryProcessor.ManualRetryAsync(batch, now, cancellationToken);

        // UC-73 inherits UC-74's preconditions ("UC-74 rules apply") — a refusal makes no change
        // to the batch and is not itself a retry attempt, so the audit outcome reflects that.
        var auditOutcome = outcome == ManualRetryOutcome.Settled ? AuditOutcome.Success : AuditOutcome.Failure;
        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            currentUserService.UserId, AuditActionTypes.PayoutSettlementRetried,
            AuditEntityTypes.PayoutBatch, batch.Id, now, auditOutcome,
            reason: outcome.ToString(),
            afterData: System.Text.Json.JsonSerializer.Serialize(new { Outcome = outcome.ToString(), ResultingStatus = batch.Status })));

        await dbContext.SaveChangesAsync(cancellationToken);

        return outcome switch
        {
            ManualRetryOutcome.RefusedOperatorLocked =>
                Result.Failure<DailyPayoutBatchActionResponseDto>(DailyPayoutErrorCodes.RetryBlockedOperatorLocked, DailyPayoutErrorCodes.RetryBlockedOperatorLockedMessage),
            ManualRetryOutcome.RefusedMissingBankAccount =>
                Result.Failure<DailyPayoutBatchActionResponseDto>(DailyPayoutErrorCodes.RetryBlockedMissingBankAccount, DailyPayoutErrorCodes.RetryBlockedMissingBankAccountMessage),
            _ => Result.Success(new DailyPayoutBatchActionResponseDto(batch.Id, batch.Status, now)),
        };
    }
}