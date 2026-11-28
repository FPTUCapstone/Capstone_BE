using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.Chat.GetGroupMessages;

public sealed class GetGroupMessagesQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<GetGroupMessagesQuery, Result<IReadOnlyList<GroupMessageDto>>>
{
    public async Task<Result<IReadOnlyList<GroupMessageDto>>> Handle(
        GetGroupMessagesQuery request,
        CancellationToken cancellationToken)
    {
        var isMember = await dbContext.GroupMembers
            .AsNoTracking()
            .AnyAsync(m => m.GroupId == request.GroupId
                && m.UserId == request.UserId
                && m.Status == GroupMemberStatus.Active,
                cancellationToken);

        if (!isMember)
        {
            var groupExists = await dbContext.TravelGroups
                .AsNoTracking()
                .AnyAsync(g => g.Id == request.GroupId, cancellationToken);

            return Result.Failure<IReadOnlyList<GroupMessageDto>>(
                groupExists ? TravelGroupErrorCodes.ActiveMembershipRequired : TravelGroupErrorCodes.GroupNotFound,
                groupExists ? "Active membership is required to read messages." : "Group not found.");
        }

        var query = dbContext.GroupMessages
            .AsNoTracking()
            .Include(m => m.SenderUser)
            .Include(m => m.ReadReceipts)
            .Include(m => m.RollCallCheckins)
            .Include(m => m.SplitBillPayments)
            .Include(m => m.PollVotes)
            .Where(m => m.GroupId == request.GroupId);

        if (request.BeforeMessageId.HasValue && request.BeforeMessageId.Value > 0)
        {
            query = query.Where(m => m.Id < request.BeforeMessageId.Value);
        }

        var messages = await query
            .OrderByDescending(m => m.Id)
            .Take(Math.Clamp(request.Limit, 1, 100))
            .ToListAsync(cancellationToken);

        var result = GroupMessageReader.Map(messages, request.UserId);

        return Result.Success<IReadOnlyList<GroupMessageDto>>(result);
    }
}