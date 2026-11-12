using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Payouts.DailySettlement.Admin;

/// <summary>Part of UC-48, scoped to the automatic lock this branch introduces (BR-189, PD-9A,
/// Q4): Staff unlocks a Tour Operator, but only once their pending debt is fully recovered.
/// Refused with MSG199 while any <see cref="TourOperatorDebt"/> is still outstanding. Manual
/// lock (BR-168) and the rest of UC-48 are out of scope for this branch.</summary>
public sealed record UnlockTourOperatorCommand(long TourOperatorUserId) : IRequest<Result<UnlockTourOperatorResponseDto>>;

public sealed record UnlockTourOperatorResponseDto(long TourOperatorUserId, string Status, DateTimeOffset UpdatedAtUtc);

public sealed class UnlockTourOperatorCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<UnlockTourOperatorCommand, Result<UnlockTourOperatorResponseDto>>
{
    public async Task<Result<UnlockTourOperatorResponseDto>> Handle(
        UnlockTourOperatorCommand request, CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is null || currentUserService.Role != nameof(UserRole.Staff))
            return Result.Failure<UnlockTourOperatorResponseDto>(DailyPayoutErrorCodes.Forbidden, DailyPayoutErrorCodes.ForbiddenMessage);

        var operatorUser = await dbContext.Users
            .FirstOrDefaultAsync(u => u.Id == request.TourOperatorUserId && u.Role == UserRole.TourOperator, cancellationToken);
        if (operatorUser is null)
            return Result.Failure<UnlockTourOperatorResponseDto>(DailyPayoutErrorCodes.OperatorNotFound, DailyPayoutErrorCodes.OperatorNotFoundMessage);

        if (operatorUser.Status != AccountStatus.Locked)
            return Result.Failure<UnlockTourOperatorResponseDto>(DailyPayoutErrorCodes.OperatorNotLocked, DailyPayoutErrorCodes.OperatorNotLockedMessage);

        var hasOutstandingDebt = await dbContext.TourOperatorDebts.AsNoTracking()
            .Where(d => d.TourOperatorUserId == request.TourOperatorUserId)
            .AnyAsync(d => d.RecoveredAmount < d.Amount, cancellationToken);
        if (hasOutstandingDebt)
            return Result.Failure<UnlockTourOperatorResponseDto>(
                DailyPayoutErrorCodes.OperatorHasOutstandingDebt, DailyPayoutErrorCodes.OperatorHasOutstandingDebtMessage); // MSG199

        var now = dateTimeProvider.UtcNow;
        operatorUser.Unlock(now);

        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            currentUserService.UserId, AuditActionTypes.TourOperatorUnlocked,
            AuditEntityTypes.User, operatorUser.Id, now, AuditOutcome.Success));

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new UnlockTourOperatorResponseDto(operatorUser.Id, operatorUser.Status.ToString(), now));
    }
}