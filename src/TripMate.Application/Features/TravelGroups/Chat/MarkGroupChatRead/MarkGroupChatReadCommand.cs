using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.Chat.MarkGroupChatRead;

public sealed record MarkGroupChatReadCommand(long GroupId, long UserId, long ThroughMessageId)
    : IRequest<Result<GroupChatReadCursorDto>>;

public sealed record GroupChatReadCursorDto(long LastReadChatMessageId);

public sealed class MarkGroupChatReadCommandHandler(IApplicationDbContext db)
    : IRequestHandler<MarkGroupChatReadCommand, Result<GroupChatReadCursorDto>>
{
    public async Task<Result<GroupChatReadCursorDto>> Handle(MarkGroupChatReadCommand request, CancellationToken cancellationToken)
    {
        if (request.GroupId <= 0 || request.UserId <= 0 || request.ThroughMessageId <= 0)
            return Result.Failure<GroupChatReadCursorDto>(TravelGroupErrorCodes.InvalidMessageContent,
                "Positive group, user and message IDs are required.");

        // SERIALIZABLE holds the membership/cursor read lock until save commits. Concurrent
        // cursor advances either observe the winner or retry a deadlock; they cannot regress it.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await db.ExecuteInSerializableTransactionAsync(async ct =>
                {
                    // The provider may replay this transaction after a transient failure.
                    // Always re-read the committed cursor rather than a previously tracked value.
                    db.ClearTrackedEntities();
                    var member = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == request.GroupId
                        && m.UserId == request.UserId && m.Status == GroupMemberStatus.Active
                        && m.User.Status == AccountStatus.Active, ct);
                    if (member is null)
                        return Result.Failure<GroupChatReadCursorDto>(TravelGroupErrorCodes.ActiveMembershipRequired,
                            "Active membership is required to mark chat read.");

                    if (!await db.GroupMessages.AsNoTracking().AnyAsync(m => m.GroupId == request.GroupId
                        && m.Id == request.ThroughMessageId, ct))
                        return Result.Failure<GroupChatReadCursorDto>(TravelGroupErrorCodes.MessageNotFound,
                            "Message not found in this group.");

                    member.AdvanceChatReadCursor(request.ThroughMessageId);
                    await db.SaveChangesAsync(ct);
                    return Result.Success(new GroupChatReadCursorDto(member.LastReadChatMessageId!.Value));
                }, cancellationToken);
            }
            catch (Exception ex) when (db.IsDeadlock(ex))
            {
                db.ClearTrackedEntities();
            }
        }
        return Result.Failure<GroupChatReadCursorDto>(TravelGroupErrorCodes.ConcurrentRetryExhausted,
            "Chat read cursor could not be advanced concurrently. Retry the request.");
    }
}