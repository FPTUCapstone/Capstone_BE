using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Payouts.DailySettlement.Admin;

/// <summary>UC-73 (PD-10): Administrator retries a failed batch. The PD-7a lookup-before-resend
/// still applies — this command only flips the batch back to Processing; the next retry sweep
/// pass picks it up and does the actual lookup/resend.</summary>
public sealed record RetryFailedPayoutBatchCommand(long BatchId) : IRequest<Result<DailyPayoutBatchActionResponseDto>>;

public sealed record DailyPayoutBatchActionResponseDto(long BatchId, string Status, DateTimeOffset UpdatedAtUtc);

public sealed class RetryFailedPayoutBatchCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider)
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
        batch.Retry(now);

        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            currentUserService.UserId, AuditActionTypes.PayoutSettlementRetried,
            AuditEntityTypes.PayoutBatch, batch.Id, now, AuditOutcome.Success));

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new DailyPayoutBatchActionResponseDto(batch.Id, batch.Status, now));
    }
}