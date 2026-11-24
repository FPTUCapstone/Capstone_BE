using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.Chat.AcknowledgeBroadcast;

public sealed record AcknowledgeBroadcastCommand(long GroupId, long MessageId, long UserId) : IRequest<Result<int>>;

public sealed class AcknowledgeBroadcastCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<AcknowledgeBroadcastCommand, Result<int>>
{
    public async Task<Result<int>> Handle(
        AcknowledgeBroadcastCommand request,
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
            return Result.Failure<int>(TravelGroupErrorCodes.GroupClosed, "Cannot acknowledge messages in a closed group.");
        }

        if (member.Status != GroupMemberStatus.Active)
        {
            return Result.Failure<int>(TravelGroupErrorCodes.ActiveMembershipRequired, "Active membership is required.");
        }

        var message = await dbContext.GroupMessages
            .Include(m => m.ReadReceipts)
            .FirstOrDefaultAsync(
                m => m.Id == request.MessageId && m.GroupId == request.GroupId,
                cancellationToken);

        if (message is null)
        {
            return Result.Failure<int>(TravelGroupErrorCodes.MessageNotFound, "Message not found.");
        }

        if (!string.Equals(message.MessageType, "broadcast", StringComparison.Ordinal))
        {
            return Result.Failure<int>(TravelGroupErrorCodes.InvalidMessageType, "Only broadcast messages can be acknowledged.");
        }

        message.AddReadReceipt(request.UserId, dateTimeProvider.UtcNow);
        await GroupChatPersistence.SaveInteractionAsync(dbContext, "UQ_GroupMessageReadReceipts_MessageUser", async () =>
        {
            var winner = await dbContext.GroupMessageReadReceipts.SingleAsync(
                row => row.MessageId == request.MessageId && row.UserId == request.UserId, cancellationToken);
            // The winning confirmation already represents the requested state.

        }, cancellationToken);

        return Result.Success(await dbContext.GroupMessageReadReceipts.AsNoTracking()
            .CountAsync(row => row.MessageId == request.MessageId, cancellationToken));
    }
}