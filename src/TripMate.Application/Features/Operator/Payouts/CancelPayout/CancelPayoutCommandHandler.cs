using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Payouts.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Payouts.CancelPayout;

public sealed class CancelPayoutCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<CancelPayoutCommand, Result<CancelPayoutResultDto>>
{
    public async Task<Result<CancelPayoutResultDto>> Handle(CancelPayoutCommand request, CancellationToken ct)
    {
        if (currentUser.UserId is not long operatorId || currentUser.Role != nameof(UserRole.TourOperator))
        {
            return Result.Failure<CancelPayoutResultDto>(
                OperatorPayoutErrorCodes.OperatorNotApproved, "An active approved Tour Operator account is required.");
        }

        // BR-105: a foreign or unknown payout code returns the same NotFound response.
        var payout = await db.Payouts.FirstOrDefaultAsync(
            p => p.PayoutCode == request.PayoutCode && p.OperatorUserId == operatorId, ct);
        if (payout is null)
        {
            return Result.Failure<CancelPayoutResultDto>(
                OperatorPayoutErrorCodes.PayoutNotFound, "The payout request was not found.");
        }

        if (!payout.Cancel(clock.UtcNow))
        {
            return Result.Failure<CancelPayoutResultDto>(
                OperatorPayoutErrorCodes.PayoutNotCancellable, $"Payout is in status '{payout.Status}' and cannot be cancelled.");
        }

        var appliedAdjustments = await db.PayoutAdjustments
            .Where(a => a.AppliedPayoutId == payout.Id)
            .ToListAsync(ct);
        foreach (var adjustment in appliedAdjustments)
        {
            adjustment.Revert();
        }

        await db.SaveChangesAsync(ct);

        return Result.Success(new CancelPayoutResultDto(payout.PayoutCode, payout.Status));
    }
}
