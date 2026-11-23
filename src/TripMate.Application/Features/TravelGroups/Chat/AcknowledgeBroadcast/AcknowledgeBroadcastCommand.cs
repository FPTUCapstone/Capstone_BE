using MediatR;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.Chat.AcknowledgeBroadcast;

public sealed record AcknowledgeBroadcastCommand(long MessageId, long UserId) : IRequest<Result<int>>;

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
            .Include(m => m.ReadReceipts)
            .FirstOrDefaultAsync(m => m.Id == request.MessageId, cancellationToken);

        if (message is null)
        {
            return Result.Failure<int>(TravelGroupErrorCodes.MessageNotFound, "Message not found.");
        }

        var isMember = await dbContext.GroupMembers
            .AsNoTracking()
            .AnyAsync(m => m.GroupId == message.GroupId
                && m.UserId == request.UserId
                && m.Status == GroupMemberStatus.Active,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure<int>(TravelGroupErrorCodes.ActiveMembershipRequired, "Active membership is required.");
        }

        message.AddReadReceipt(request.UserId, dateTimeProvider.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(message.ReadReceipts.Count);
    }
}
