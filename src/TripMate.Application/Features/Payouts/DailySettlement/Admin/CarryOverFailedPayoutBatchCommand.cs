using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Payouts.DailySettlement.Admin;

/// <summary>UC-73 (PD-10): Administrator carries a failed batch's amount over to the next run of
/// the same operator. One <see cref="PayoutSettlementAdjustment"/> is recorded per settlement
/// with a positive payout amount — picked up by the next <see cref="DailyPayoutRunProcessor"/>
/// run for this operator (PD-10, PD-14).</summary>
public sealed record CarryOverFailedPayoutBatchCommand(long BatchId) : IRequest<Result<DailyPayoutBatchActionResponseDto>>;

public sealed class CarryOverFailedPayoutBatchCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<CarryOverFailedPayoutBatchCommand, Result<DailyPayoutBatchActionResponseDto>>
{
    public async Task<Result<DailyPayoutBatchActionResponseDto>> Handle(
        CarryOverFailedPayoutBatchCommand request, CancellationToken cancellationToken)
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

        foreach (var settlement in batch.Settlements.Where(s => s.PayoutAmount > 0))
        {
            dbContext.PayoutSettlementAdjustments.Add(PayoutSettlementAdjustment.CreateCarryOver(
                settlement.Id, batch.TourOperatorUserId, settlement.PayoutAmount, now));
        }

        batch.MarkCarriedOver(now);

        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            currentUserService.UserId, AuditActionTypes.PayoutSettlementCarriedOver,
            AuditEntityTypes.PayoutBatch, batch.Id, now, AuditOutcome.Success));

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new DailyPayoutBatchActionResponseDto(batch.Id, batch.Status, now));
    }
}
