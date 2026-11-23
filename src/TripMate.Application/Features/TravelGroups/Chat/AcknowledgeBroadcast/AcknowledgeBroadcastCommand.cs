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
        var message = await dbContext.GroupMessages
            .Include(m => m.Group)
            .Include(m => m.ReadReceipts)
            .FirstOrDefaultAsync(
                m => m.Id == request.MessageId && m.GroupId == request.GroupId,
                cancellationToken);

        if (message is null)
        {
            return Result.Failure<int>(TravelGroupErrorCodes.MessageNotFound, "Message not found.");
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

        if (message.Group.Status != TravelGroupStatus.Active)
        {
            return Result.Failure<int>(TravelGroupErrorCodes.GroupClosed, "Cannot acknowledge messages in a closed group.");
        }

        if (member.Status != GroupMemberStatus.Active)
        {
            return Result.Failure<int>(TravelGroupErrorCodes.ActiveMembershipRequired, "Active membership is required.");
        }

        if (!string.Equals(message.MessageType, "broadcast", StringComparison.Ordinal))
        {
            return Result.Failure<int>(TravelGroupErrorCodes.InvalidMessageType, "Only broadcast messages can be acknowledged.");
        }

        message.AddReadReceipt(request.UserId, dateTimeProvider.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(message.ReadReceipts.Count);
    }
}