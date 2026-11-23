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
        var group = await dbContext.TravelGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == request.GroupId, cancellationToken);

        if (group is null)
        {
            return Result.Failure<GroupMessageDto>(
                TravelGroupErrorCodes.GroupNotFound,
                "Group not found.");
        }

        var groupMember = await dbContext.GroupMembers
            .Include(m => m.User)
            .FirstOrDefaultAsync(
                m => m.GroupId == request.GroupId && m.UserId == request.SenderUserId,
                cancellationToken);

        if (groupMember is null || groupMember.Status == GroupMemberStatus.Removed)
        {
            return Result.Failure<GroupMessageDto>(
                TravelGroupErrorCodes.ActiveMembershipRequired,
                "Active membership is required to send messages.");
        }

        if (group.Status != TravelGroupStatus.Active)
        {
            return Result.Failure<GroupMessageDto>(
                TravelGroupErrorCodes.GroupClosed,
                "Cannot send messages to a closed group.");
        }

        if (groupMember.Status != GroupMemberStatus.Active)
        {
            return Result.Failure<GroupMessageDto>(
                TravelGroupErrorCodes.ActiveMembershipRequired,
                "Active membership is required to send messages.");
        }

        var normalizedType = request.MessageType.Trim().ToLowerInvariant();
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