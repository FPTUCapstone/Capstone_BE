using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Payouts.DailySettlement.Admin;

/// <summary>UC-73 (PD-10): Administrator records a transfer made outside the system for a failed
/// batch. The amount is never recalculated (BR-146) — only the status and the reference change.</summary>
public sealed record RecordManualPayoutSettlementCommand(long BatchId, string? TransferReference)
    : IRequest<Result<DailyPayoutBatchActionResponseDto>>;

public sealed class RecordManualPayoutSettlementCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<RecordManualPayoutSettlementCommand, Result<DailyPayoutBatchActionResponseDto>>
{
    public async Task<Result<DailyPayoutBatchActionResponseDto>> Handle(
        RecordManualPayoutSettlementCommand request, CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is null || currentUserService.Role != nameof(UserRole.Administrator))
            return Result.Failure<DailyPayoutBatchActionResponseDto>(DailyPayoutErrorCodes.Forbidden, DailyPayoutErrorCodes.ForbiddenMessage);

        if (string.IsNullOrWhiteSpace(request.TransferReference))
            return Result.Failure<DailyPayoutBatchActionResponseDto>(
                DailyPayoutErrorCodes.ManualReferenceRequired, DailyPayoutErrorCodes.ManualReferenceRequiredMessage);

        var batch = await dbContext.PayoutBatches
            .Include(b => b.Settlements)
            .FirstOrDefaultAsync(b => b.Id == request.BatchId, cancellationToken);
        if (batch is null)
            return Result.Failure<DailyPayoutBatchActionResponseDto>(DailyPayoutErrorCodes.BatchNotFound, DailyPayoutErrorCodes.BatchNotFoundMessage);

        if (batch.Status != PayoutSettlementStatus.SettlementFailed)
            return Result.Failure<DailyPayoutBatchActionResponseDto>(DailyPayoutErrorCodes.NotFailed, DailyPayoutErrorCodes.NotFailedMessage);

        var now = dateTimeProvider.UtcNow;
        batch.MarkSettledManually(request.TransferReference.Trim(), now);

        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            currentUserService.UserId, AuditActionTypes.PayoutSettlementManuallySettled,
            AuditEntityTypes.PayoutBatch, batch.Id, now, AuditOutcome.Success,
            afterData: System.Text.Json.JsonSerializer.Serialize(new { TransferReference = request.TransferReference.Trim() })));

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new DailyPayoutBatchActionResponseDto(batch.Id, batch.Status, now));
    }
}
