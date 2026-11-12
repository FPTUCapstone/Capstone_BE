using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Payouts.DailySettlement.Admin;

/// <summary>UC-73 (spec v6, PD-10): Administrator [Mark as Resolved] action on a permanently
/// failed batch (SettlementFailed, out of automatic retries). No money moves; the amount is
/// never recalculated (BR-146). A reason is required (MSG178) and is recorded on the audit log,
/// not on the batch itself.</summary>
public sealed record MarkPayoutBatchResolvedCommand(long BatchId, string? Reason)
    : IRequest<Result<DailyPayoutBatchActionResponseDto>>;

public sealed class MarkPayoutBatchResolvedCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<MarkPayoutBatchResolvedCommand, Result<DailyPayoutBatchActionResponseDto>>
{
    public async Task<Result<DailyPayoutBatchActionResponseDto>> Handle(
        MarkPayoutBatchResolvedCommand request, CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is null || currentUserService.Role != nameof(UserRole.Administrator))
            return Result.Failure<DailyPayoutBatchActionResponseDto>(DailyPayoutErrorCodes.Forbidden, DailyPayoutErrorCodes.ForbiddenMessage);

        if (string.IsNullOrWhiteSpace(request.Reason))
            return Result.Failure<DailyPayoutBatchActionResponseDto>(
                DailyPayoutErrorCodes.ResolutionReasonRequired, DailyPayoutErrorCodes.ResolutionReasonRequiredMessage);

        var batch = await dbContext.PayoutBatches
            .Include(b => b.Settlements)
            .FirstOrDefaultAsync(b => b.Id == request.BatchId, cancellationToken);
        if (batch is null)
            return Result.Failure<DailyPayoutBatchActionResponseDto>(DailyPayoutErrorCodes.BatchNotFound, DailyPayoutErrorCodes.BatchNotFoundMessage);

        if (batch.Status != PayoutSettlementStatus.SettlementFailed)
            return Result.Failure<DailyPayoutBatchActionResponseDto>(DailyPayoutErrorCodes.NotFailed, DailyPayoutErrorCodes.NotFailedMessage);

        var now = dateTimeProvider.UtcNow;
        var reason = request.Reason.Trim();
        batch.MarkResolved(reason, now);

        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            currentUserService.UserId, AuditActionTypes.PayoutSettlementResolved,
            AuditEntityTypes.PayoutBatch, batch.Id, now, AuditOutcome.Success,
            afterData: System.Text.Json.JsonSerializer.Serialize(new { Reason = reason })));

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new DailyPayoutBatchActionResponseDto(batch.Id, batch.Status, now));
    }
}
