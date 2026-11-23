using MediatR;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.Chat.SendGroupMessage;

public sealed class SendGroupMessageCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<SendGroupMessageCommand, Result<GroupMessageDto>>
{
    public async Task<Result<GroupMessageDto>> Handle(
        SendGroupMessageCommand request,
        CancellationToken cancellationToken)
    {
        var groupMember = await dbContext.GroupMembers
            .Include(m => m.User)
            .Include(m => m.TravelGroup)
            .FirstOrDefaultAsync(m => m.GroupId == request.GroupId
                && m.UserId == request.SenderUserId
                && m.Status == GroupMemberStatus.Active,
                cancellationToken);

        if (groupMember is null)
        {
            var groupExists = await dbContext.TravelGroups
                .AsNoTracking()
                .AnyAsync(g => g.Id == request.GroupId, cancellationToken);

            return Result.Failure<GroupMessageDto>(
                groupExists ? TravelGroupErrorCodes.ActiveMembershipRequired : TravelGroupErrorCodes.GroupNotFound,
                groupExists ? "Active membership is required to send messages." : "Group not found.");
        }

        var normalizedType = request.MessageType.ToLowerInvariant().Trim();
        var now = dateTimeProvider.UtcNow;

        var message = new GroupMessage(
            request.GroupId,
            request.SenderUserId,
            normalizedType,
            request.Content.Trim(),
            request.MetadataJson,
            now);

        // For split bill, creator is initially marked as paid
        if (normalizedType == "split_bill")
        {
            message.UpdateSplitBillPayment(request.SenderUserId, true, now);
        }

        dbContext.GroupMessages.Add(message);
        await dbContext.SaveChangesAsync(cancellationToken);

        var dto = new GroupMessageDto(
            message.Id,
            message.GroupId,
            message.SenderUserId,
            groupMember.User.FullName,
            groupMember.User.AvatarUrl,
            message.MessageType,
            message.Content,
            message.MetadataJson,
            message.CreatedAtUtc,
            0,
            false,
            0,
            false,
            normalizedType == "split_bill" ? [request.SenderUserId] : [],
            new Dictionary<int, int>(),
            null);

        return Result.Success(dto);
    }
}
