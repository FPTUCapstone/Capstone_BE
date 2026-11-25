using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.Chat.CheckinRollCall;

public sealed record CheckinRollCallCommand(long GroupId, long MessageId, long UserId) : IRequest<Result<int>>;

public sealed class CheckinRollCallCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<CheckinRollCallCommand, Result<int>>
{
    public async Task<Result<int>> Handle(
        CheckinRollCallCommand request,
        CancellationToken cancellationToken)
    {
        var group = await dbContext.TravelGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == request.GroupId, cancellationToken);

        if (group is null)
        {
            return Result.Failure<int>(TravelGroupErrorCodes.GroupNotFound, "Group not found.");
        }

        var member = await dbContext.GroupMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.GroupId == request.GroupId && m.UserId == request.UserId,
                cancellationToken);

        if (member is null || member.Status == GroupMemberStatus.Removed)
        {
            return Result.Failure<int>(TravelGroupErrorCodes.ActiveMembershipRequired, "Active membership is required.");
        }

        if (group.Status != TravelGroupStatus.Active)
        {
            return Result.Failure<int>(TravelGroupErrorCodes.GroupClosed, "Cannot check in to a closed group.");
        }

        if (member.Status != GroupMemberStatus.Active)
        {
            return Result.Failure<int>(TravelGroupErrorCodes.ActiveMembershipRequired, "Active membership is required.");
        }

        var message = await dbContext.GroupMessages
            .Include(m => m.RollCallCheckins)
            .FirstOrDefaultAsync(
                m => m.Id == request.MessageId && m.GroupId == request.GroupId,
                cancellationToken);

        if (message is null)
        {
            return Result.Failure<int>(TravelGroupErrorCodes.MessageNotFound, "Message not found.");
        }

        if (!string.Equals(message.MessageType, "roll_call", StringComparison.Ordinal))
        {
            return Result.Failure<int>(TravelGroupErrorCodes.InvalidMessageType, "Only roll call messages support check-in.");
        }

        message.AddRollCallCheckin(request.UserId, dateTimeProvider.UtcNow);
        await GroupChatPersistence.SaveInteractionAsync(dbContext, "UQ_GroupRollCallCheckins_MessageUser", async () =>
        {
            var winner = await dbContext.GroupRollCallCheckins.SingleAsync(
                row => row.MessageId == request.MessageId && row.UserId == request.UserId, cancellationToken);
            // The winning confirmation already represents the requested state.

        }, cancellationToken);

        return Result.Success(await dbContext.GroupRollCallCheckins.AsNoTracking()
            .CountAsync(row => row.MessageId == request.MessageId, cancellationToken));
    }
}