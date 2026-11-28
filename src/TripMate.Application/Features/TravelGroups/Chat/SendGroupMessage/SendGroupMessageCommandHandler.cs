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
        var validation = await new SendGroupMessageCommandValidator().ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
            return Result.Failure<GroupMessageDto>(validation.Errors[0].ErrorCode, validation.Errors[0].ErrorMessage);

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
        if (normalizedType is "broadcast" or "roll_call")
        {
            var isAuthorizedSender = group.HostUserId == request.SenderUserId
                || groupMember.User.Role == UserRole.TourOperator;
            if (!isAuthorizedSender)
            {
                return Result.Failure<GroupMessageDto>(
                    TravelGroupErrorCodes.HostPermissionRequired,
                    "Only the group host or tour operator can send broadcast or roll call messages.");
            }
        }

        if (normalizedType == "split_bill")
        {
            if (!GroupChatMetadata.TryGetSplitBillParticipantIds(request.MetadataJson, out var participantIds))
            {
                return Result.Failure<GroupMessageDto>(
                    TravelGroupErrorCodes.InvalidMessageContent,
                    "Split bill requires valid recipient bank details and participant amounts matching totalAmount.");
            }

            var activeMemberIds = await dbContext.GroupMembers
                .AsNoTracking()
                .Where(m => m.GroupId == request.GroupId && m.Status == GroupMemberStatus.Active)
                .Select(m => m.UserId)
                .ToListAsync(cancellationToken);

            var activeSet = activeMemberIds.ToHashSet();
            if (participantIds.Any(id => !activeSet.Contains(id)))
            {
                return Result.Failure<GroupMessageDto>(
                    TravelGroupErrorCodes.InvalidMessageContent,
                    "All split bill participants must be active members of this group.");
            }
        }

        if (request.ClientMessageId.HasValue)
        {
            var existing = await FindReplayAsync(request, cancellationToken);
            if (existing is not null) return await ReplayAsync(existing, request, cancellationToken);
        }
        var now = dateTimeProvider.UtcNow;

        var message = new GroupMessage(
            request.GroupId,
            request.SenderUserId,
            normalizedType,
            request.Content.Trim(),
            request.MetadataJson,
            now,
            request.ClientMessageId);

        // For split bill, creator is initially marked as paid
        if (normalizedType == "split_bill")
        {
            message.UpdateSplitBillPayment(request.SenderUserId, true, now, isInitialCreatorMarker: true);
        }

        dbContext.GroupMessages.Add(message);
        try { await dbContext.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (request.ClientMessageId.HasValue
            && GroupChatPersistence.IsUniqueConflict(ex, GroupChatPersistence.MessageRetryIndex))
        {
            dbContext.ClearTrackedEntities();
            var winner = await FindReplayAsync(request, cancellationToken);
            if (winner is null) throw;
            return await ReplayAsync(winner, request, cancellationToken);
        }

        return await LoadMessageAsync(message, request, cancellationToken);
    }

    private Task<GroupMessage?> FindReplayAsync(SendGroupMessageCommand request, CancellationToken ct) =>
        dbContext.GroupMessages.AsNoTracking().FirstOrDefaultAsync(m => m.GroupId == request.GroupId
            && m.SenderUserId == request.SenderUserId && m.ClientMessageId == request.ClientMessageId, ct);

    private async Task<Result<GroupMessageDto>> ReplayAsync(GroupMessage message, SendGroupMessageCommand request, CancellationToken ct)
    {
        if (message.MessageType != request.MessageType.Trim().ToLowerInvariant()
            || (message.OriginalContent ?? message.Content) != request.Content.Trim()
            || GroupChatMetadata.Normalize(message.OriginalContent is null ? message.MetadataJson : message.OriginalMetadataJson) != GroupChatMetadata.Normalize(request.MetadataJson))
            return Result.Failure<GroupMessageDto>(TravelGroupErrorCodes.IdempotencyKeyPayloadMismatch,
                "Client message ID was already used for a different payload.");

        return await LoadMessageAsync(message, request, ct);
    }

    private async Task<Result<GroupMessageDto>> LoadMessageAsync(GroupMessage message, SendGroupMessageCommand request, CancellationToken ct)
    {
        var member = await dbContext.GroupMembers.AsNoTracking().AnyAsync(m => m.GroupId == request.GroupId
            && m.UserId == request.SenderUserId && m.Status == GroupMemberStatus.Active, ct);
        if (!member) return Result.Failure<GroupMessageDto>(TravelGroupErrorCodes.ActiveMembershipRequired, "Active membership is required to read messages.");
        var items = await GroupMessageReader.ReadAsync(dbContext, request.GroupId, request.SenderUserId, [message.Id], ct);
        var item = items.SingleOrDefault();
        return item is null ? Result.Failure<GroupMessageDto>(TravelGroupErrorCodes.MessageNotFound, "Message not found.") : Result.Success(item);
    }
}