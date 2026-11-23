using MediatR;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.Chat.CheckinRollCall;

public sealed record CheckinRollCallCommand(long MessageId, long UserId) : IRequest<Result<int>>;

public sealed class CheckinRollCallCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<CheckinRollCallCommand, Result<int>>
{
    public async Task<Result<int>> Handle(
        CheckinRollCallCommand request,
        CancellationToken cancellationToken)
    {
        var message = await dbContext.GroupMessages
            .Include(m => m.RollCallCheckins)
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

        message.AddRollCallCheckin(request.UserId, dateTimeProvider.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(message.RollCallCheckins.Count);
    }
}
