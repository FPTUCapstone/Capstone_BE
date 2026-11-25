using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.PendingChanges.Discard;

/// <summary>Deletes an open (Draft/Rejected) draft version outright (spec TM-82 §5.4).</summary>
public sealed class DiscardPendingChangeCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<DiscardPendingChangeCommand, Result>
{
    public async Task<Result> Handle(DiscardPendingChangeCommand request, CancellationToken cancellationToken)
    {
        if (!currentUser.UserId.HasValue)
            return Result.Failure(OperatorTourErrorCodes.AccessRequired, "An active approved Tour Operator account is required.");
        long operatorId = currentUser.UserId.Value;

        bool eligible = await dbContext.OperatorProfiles.AsNoTracking().AnyAsync(profile =>
            profile.UserId == operatorId && profile.ApprovalStatus == OperatorApprovalStatus.Approved &&
            profile.User.Role == UserRole.TourOperator && profile.User.Status == AccountStatus.Active,
            cancellationToken);
        if (!eligible)
            return Result.Failure(OperatorTourErrorCodes.AccessRequired, "An active approved Tour Operator account is required.");

        Tour? tour = await dbContext.Tours.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == request.TourId && candidate.OperatorUserId == operatorId,
            cancellationToken);
        if (tour is null)
            return Result.Failure(OperatorTourErrorCodes.TourNotFound, "The tour is unavailable.");

        TourPendingChangeSet? changeSet = await dbContext.TourPendingChangeSets.SingleOrDefaultAsync(
            candidate => candidate.Id == request.PendingChangeId && candidate.TourId == tour.Id,
            cancellationToken);
        if (changeSet is null)
            return Result.Failure(OperatorTourErrorCodes.PendingChangeNotFound, "The draft version is unavailable.");
        if (changeSet.Status is not (TourPendingChangeStatus.Draft or TourPendingChangeStatus.Rejected))
        {
            return Result.Failure(OperatorTourErrorCodes.InvalidState,
                "MSG122: the draft version is currently pending approval.");
        }

        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(operatorId,
            AuditActionTypes.TourPendingChangeDiscarded, AuditEntityTypes.Tour, tour.Id, clock.UtcNow,
            AuditOutcome.Success));
        dbContext.TourPendingChangeSets.Remove(changeSet);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
