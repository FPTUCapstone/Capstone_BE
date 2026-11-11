using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Payouts.DailySettlement.Reads;

/// <summary>UC-73: the gateway failure information and attempt history for one failed batch.</summary>
public sealed record GetFailedPayoutBatchDetailQuery(long BatchId) : IRequest<Result<FailedPayoutBatchDetailDto>>;

public sealed record PayoutSettlementAttemptDto(int AttemptNumber, DateTimeOffset SentAtUtc, string Outcome, string? FailureReason);

public sealed record FailedPayoutBatchDetailDto(
    long BatchId,
    long TourOperatorUserId,
    DateOnly RunDate,
    decimal TotalAmount,
    string Status,
    int AttemptCount,
    IReadOnlyList<PayoutSettlementAttemptDto> Attempts);

public sealed class GetFailedPayoutBatchDetailQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUserService)
    : IRequestHandler<GetFailedPayoutBatchDetailQuery, Result<FailedPayoutBatchDetailDto>>
{
    public async Task<Result<FailedPayoutBatchDetailDto>> Handle(GetFailedPayoutBatchDetailQuery request, CancellationToken cancellationToken)
    {
        if (currentUserService.Role != nameof(UserRole.Administrator))
            return Result.Failure<FailedPayoutBatchDetailDto>(DailyPayoutErrorCodes.Forbidden, DailyPayoutErrorCodes.ForbiddenMessage);

        var batch = await dbContext.PayoutBatches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == request.BatchId, cancellationToken);
        if (batch is null)
            return Result.Failure<FailedPayoutBatchDetailDto>(DailyPayoutErrorCodes.BatchNotFound, DailyPayoutErrorCodes.BatchNotFoundMessage);

        var attempts = await dbContext.PayoutSettlementAttempts.AsNoTracking()
            .Where(a => a.BatchId == batch.Id)
            .OrderBy(a => a.AttemptNumber)
            .Select(a => new PayoutSettlementAttemptDto(a.AttemptNumber, a.SentAtUtc, a.Outcome, a.FailureReason))
            .ToListAsync(cancellationToken);

        return Result.Success(new FailedPayoutBatchDetailDto(
            batch.Id, batch.TourOperatorUserId, batch.RunDate, batch.TotalAmount, batch.Status, batch.AttemptCount, attempts));
    }
}