using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.LeaveTravelGroup;

public sealed class LeaveTravelGroupCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider,
    IGroupInvitationLock invitationLock)
    : IRequestHandler<LeaveTravelGroupCommand, Result>
{
    public Task<Result> Handle(LeaveTravelGroupCommand request, CancellationToken cancellationToken) =>
        dbContext.ExecuteInSerializableTransactionAsync(
            transactionCancellationToken => LeaveInTransactionAsync(request, transactionCancellationToken),
            cancellationToken);

    private async Task<Result> LeaveInTransactionAsync(
        LeaveTravelGroupCommand request,
        CancellationToken cancellationToken)
    {
        await invitationLock.AcquireGroupLockAsync(request.GroupId, cancellationToken);

        var group = await dbContext.TravelGroups.FirstOrDefaultAsync(
            entity => entity.Id == request.GroupId,
            cancellationToken);
        if (group is null)
        {
            return Result.Failure(
                TravelGroupErrorCodes.GroupNotFound,
                "The specified travel group does not exist.");
        }

        if (group.ClosedAtUtc is not null)
        {
            return Result.Failure(
                TravelGroupErrorCodes.GroupClosed,
                "The travel group is closed.");
        }

        var membership = await dbContext.GroupMembers.FirstOrDefaultAsync(
            member => member.GroupId == request.GroupId && member.UserId == request.CurrentUserId,
            cancellationToken);
        if (membership is null || membership.Status != GroupMemberStatus.Active)
        {
            return Result.Failure(
                TravelGroupErrorCodes.ActiveMembershipRequired,
                "You must be an active member of this travel group to leave it.");
        }

        var hasOtherActiveMembers = await dbContext.GroupMembers.AnyAsync(
            member => member.GroupId == request.GroupId
                && member.UserId != request.CurrentUserId
                && member.Status == GroupMemberStatus.Active,
            cancellationToken);
        if (group.HostUserId == request.CurrentUserId && hasOtherActiveMembers)
        {
            return Result.Failure(
                TravelGroupErrorCodes.HostTransferRequired,
                "Transfer the Group Host role before leaving a group with active members.");
        }

        var now = dateTimeProvider.UtcNow;
        membership.Leave(now);

        if (group.HostUserId == request.CurrentUserId)
        {
            group.Close(now);

            var invitations = await dbContext.GroupInvitations
                .Where(invitation => invitation.GroupId == request.GroupId)
                .ToListAsync(cancellationToken);
            foreach (var invitation in invitations)
            {
                invitation.Expire(now);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}