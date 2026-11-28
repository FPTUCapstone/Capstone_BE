using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Chat;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
namespace TripMate.Application.Features.ChatGroups;

public sealed class ChatGroupHandlers(IApplicationDbContext db, IDateTimeProvider clock) :
    IRequestHandler<RenameChatGroup, Result<ChatGroupConversationDto>>,
    IRequestHandler<KickChatGroupMember, Result<bool>>,
    IRequestHandler<DissolveChatGroup, Result<bool>>,
    IRequestHandler<GetChatGroupMembers, Result<ChatGroupMembersDto>>,
    IRequestHandler<CreateChatGroup, Result<ChatGroupConversationDto>>,
    IRequestHandler<ListChatGroups, Result<ChatGroupPage>>,
    IRequestHandler<GetChatGroupMessages, Result<IReadOnlyList<ChatGroupMessageDto>>>,
    IRequestHandler<SendChatGroupMessage, Result<ChatGroupMessageDto>>,
    IRequestHandler<LeaveChatGroup, Result<ChatGroupLeaveReceipt>>,
    IRequestHandler<ReadChatGroup, Result>
{
    private Task<bool> Active(long id, CancellationToken ct) => db.Users.AsNoTracking().AnyAsync(u => u.Id == id && u.Status == AccountStatus.Active, ct);
    private IQueryable<ChatGroup> Accessible(long actor) => db.ChatGroups.Where(g => g.Members.Any(m => m.UserId == actor));
    private static Result<T> Invalid<T>() => Result.Failure<T>(ChatGroupErrors.Invalid, "Invalid chat group request.");
    private static Result<T> Missing<T>() => Result.Failure<T>(ChatGroupErrors.NotFound, "Chat group not found.");
    private static Result<T> Forbidden<T>() => Result.Failure<T>(ChatGroupErrors.Forbidden, "An Active account is required.");
    private static Result<T> Closed<T>() => Result.Failure<T>(ChatGroupErrors.Conflict, "This chat group is closed.");
    private static Result<T> HostRequired<T>() => Result.Failure<T>(ChatGroupErrors.Forbidden, "Only the current host may manage this chat group.");
    private static bool ValidPage(int limit, long? before) => limit is >= 1 and <= 100 && (before is null || before > 0);
    private IQueryable<ChatGroupConversationDto> Project(IQueryable<ChatGroup> groups, long actor) => groups.AsNoTracking().Select(g =>
        new ChatGroupConversationDto(g.Id, g.GroupName, g.Members.Count, g.HostUserId,
            db.ChatGroupMessages.Where(m => m.GroupId == g.Id).OrderByDescending(m => m.Id).Select(m => m.Content).FirstOrDefault(),
            db.ChatGroupMessages.Where(m => m.GroupId == g.Id).OrderByDescending(m => m.Id).Select(m => (DateTimeOffset?)m.CreatedAtUtc).FirstOrDefault(),
            db.ChatGroupMessages.Count(m => m.GroupId == g.Id && m.SenderUserId != actor && m.Id >
                g.Members.Where(member => member.UserId == actor).Select(member => member.ReadThroughMessageId).First()), g.IsClosed));

    public async Task<Result<ChatGroupConversationDto>> Handle(CreateChatGroup r, CancellationToken ct)
    {
        if (!await Active(r.ActorId, ct)) return Forbidden<ChatGroupConversationDto>();
        var name = r.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > ChatGroup.MaxNameLength || r.ClientRequestId == Guid.Empty || r.MemberUserIds is null)
            return Invalid<ChatGroupConversationDto>();
        var ids = r.MemberUserIds.Append(r.ActorId).Distinct().Order().ToArray();
        if (ids.Length is < 3 or > ChatGroup.MaxMembers || ids.Any(i => i <= 0)) return Invalid<ChatGroupConversationDto>();
        var group = await db.ChatGroups.Include(g => g.Members).FirstOrDefaultAsync(g => g.CreatedByUserId == r.ActorId && g.ClientRequestId == r.ClientRequestId, ct);
        if (group is null)
        {
            if (await db.Users.CountAsync(u => ids.Contains(u.Id) && u.Status == AccountStatus.Active, ct) != ids.Length)
                return Invalid<ChatGroupConversationDto>();
            group = new ChatGroup(r.ActorId, name, ids, r.ClientRequestId, clock.UtcNow);
            db.ChatGroups.Add(group);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                // SaveChanges transaction rolls back the entire group/member graph. Only a durable winner can be adopted.
                db.ClearTrackedEntities();
                group = await db.ChatGroups.Include(g => g.Members).FirstOrDefaultAsync(g => g.CreatedByUserId == r.ActorId && g.ClientRequestId == r.ClientRequestId, ct);
                if (group is null) throw;
            }
        }
        // Re-read under the same lock as rename/kick/leave/dissolve so a replay returns a
        // current authorized DTO, even when the creator's membership changes concurrently.
        var groupId = group.Id;
        return await InGroupTransaction(groupId, async token =>
        {
            if (!await Active(r.ActorId, token)) return Forbidden<ChatGroupConversationDto>();
            var current = await db.ChatGroups.AsNoTracking().Include(g => g.Members).SingleAsync(g => g.Id == groupId, token);
            if (!string.Equals(current.InitialGroupName, name, StringComparison.Ordinal)
                || !string.Equals(current.InitialMemberUserIds, string.Join(",", ids), StringComparison.Ordinal))
                return Result.Failure<ChatGroupConversationDto>(ChatGroupErrors.Conflict, "Client request ID was already used with different group details.");
            if (!current.Members.Any(m => m.UserId == r.ActorId))
                return Result.Failure<ChatGroupConversationDto>(ChatGroupErrors.Conflict, "This creation request already succeeded, but you have left the group.");
            return Result.Success(await Project(Accessible(r.ActorId).Where(g => g.Id == groupId), r.ActorId).SingleAsync(token));
        }, ct);
    }

    public async Task<Result<ChatGroupPage>> Handle(ListChatGroups r, CancellationToken ct)
    {
        if (!await Active(r.ActorId, ct)) return Forbidden<ChatGroupPage>();
        if (!ValidPage(r.Limit, r.BeforeGroupId)) return Invalid<ChatGroupPage>();
        var items = await Project(Accessible(r.ActorId).Where(g => r.BeforeGroupId == null || g.Id < r.BeforeGroupId)
            .OrderByDescending(g => g.Id).Take(r.Limit + 1), r.ActorId).ToListAsync(ct);
        var more = items.Count > r.Limit;
        if (more) items.RemoveAt(items.Count - 1);
        return Result.Success(new ChatGroupPage(items, more ? items[^1].GroupId : null));
    }
    private static ChatGroupMessageDto ToDto(ChatGroupMessage m, string name) => new(m.Id, m.GroupId, m.SenderUserId, name, m.Content, m.CreatedAtUtc, m.ClientMessageId.ToString("D"));

    public Task<Result<IReadOnlyList<ChatGroupMessageDto>>> Handle(GetChatGroupMessages r, CancellationToken ct) =>
        InGroupTransaction(r.GroupId, token => HandleCore(r, token), ct);

    private async Task<Result<IReadOnlyList<ChatGroupMessageDto>>> HandleCore(GetChatGroupMessages r, CancellationToken ct)
    {
        if (!await Active(r.ActorId, ct)) return Forbidden<IReadOnlyList<ChatGroupMessageDto>>();
        if (r.GroupId <= 0 || !ValidPage(r.Limit, r.BeforeMessageId)) return Invalid<IReadOnlyList<ChatGroupMessageDto>>();
        if (!await Accessible(r.ActorId).AnyAsync(g => g.Id == r.GroupId, ct)) return Missing<IReadOnlyList<ChatGroupMessageDto>>();
        var items = await db.ChatGroupMessages.AsNoTracking().Include(m => m.SenderUser)
            .Where(m => m.GroupId == r.GroupId && (r.BeforeMessageId == null || m.Id < r.BeforeMessageId))
            .OrderByDescending(m => m.Id).Take(r.Limit).ToListAsync(ct);
        return Result.Success<IReadOnlyList<ChatGroupMessageDto>>(items.OrderBy(m => m.Id).Select(m => ToDto(m, m.SenderUser.FullName)).ToArray());
    }
    public Task<Result<ChatGroupMessageDto>> Handle(SendChatGroupMessage r, CancellationToken ct) =>
        InGroupTransaction(r.GroupId, token => HandleCore(r, token), ct);

    private async Task<Result<ChatGroupMessageDto>> HandleCore(SendChatGroupMessage r, CancellationToken ct)
    {
        if (!await Active(r.ActorId, ct)) return Forbidden<ChatGroupMessageDto>();
        var content = r.Content?.Trim();
        if (r.GroupId <= 0 || string.IsNullOrEmpty(content) || content.Length > ChatGroupMessage.MaxContentLength || r.ClientMessageId == Guid.Empty)
            return Invalid<ChatGroupMessageDto>();
        var group = await Accessible(r.ActorId).Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == r.GroupId, ct);
        if (group is null) return Missing<ChatGroupMessageDto>();
        if (group.IsClosed) return Closed<ChatGroupMessageDto>();
        var message = await db.ChatGroupMessages.FirstOrDefaultAsync(m => m.GroupId == r.GroupId && m.SenderUserId == r.ActorId && m.ClientMessageId == r.ClientMessageId, ct);
        if (message is null)
        {
            message = new ChatGroupMessage(group, r.ActorId, content, r.ClientMessageId, clock.UtcNow);
            db.ChatGroupMessages.Add(message);
            await db.SaveChangesAsync(ct);
        }
        if (!string.Equals(message.Content, content, StringComparison.Ordinal))
            return Result.Failure<ChatGroupMessageDto>(ChatGroupErrors.Conflict, "Client message ID was already used with different content.");
        var name = await db.Users.Where(u => u.Id == r.ActorId).Select(u => u.FullName).SingleAsync(ct);
        return Result.Success(ToDto(message, name));
    }
    public async Task<Result> Handle(ReadChatGroup r, CancellationToken ct)
    {
        var result = await InGroupTransaction(r.GroupId, async token =>
        {
            var read = await ReadCore(r, token);
            return read.IsSuccess ? Result.Success(true) : Result.Failure<bool>(read.ErrorCode!, read.ErrorMessage!);
        }, ct);
        return result.IsSuccess ? Result.Success() : Result.Failure(result.ErrorCode!, result.ErrorMessage!);
    }

    private async Task<Result> ReadCore(ReadChatGroup r, CancellationToken ct)
    {
        if (!await Active(r.ActorId, ct)) return Result.Failure(ChatGroupErrors.Forbidden, "An Active account is required.");
        if (r.GroupId <= 0 || r.ThroughMessageId <= 0) return Result.Failure(ChatGroupErrors.Invalid, "Positive group and message IDs are required.");
        if (!await Accessible(r.ActorId).AnyAsync(g => g.Id == r.GroupId, ct)) return Result.Failure(ChatGroupErrors.NotFound, "Chat group not found.");
        if (!await db.ChatGroupMessages.AnyAsync(m => m.GroupId == r.GroupId && m.Id == r.ThroughMessageId, ct))
            return Result.Failure(ChatGroupErrors.Invalid, "Through message must belong to this group.");
        var member = await db.ChatGroupMembers.SingleAsync(m => m.GroupId == r.GroupId && m.UserId == r.ActorId, ct);
        member.MarkRead(r.ThroughMessageId);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public Task<Result<ChatGroupMembersDto>> Handle(GetChatGroupMembers r, CancellationToken ct) =>
        InGroupTransaction(r.GroupId, token => HandleCore(r, token), ct);

    private async Task<Result<ChatGroupMembersDto>> HandleCore(GetChatGroupMembers r, CancellationToken ct)
    {
        if (!await Active(r.ActorId, ct)) return Forbidden<ChatGroupMembersDto>();
        var group = await Accessible(r.ActorId).AsNoTracking().SingleOrDefaultAsync(g => g.Id == r.GroupId, ct);
        if (group is null) return Missing<ChatGroupMembersDto>();
        var members = await db.ChatGroupMembers.AsNoTracking().Where(m => m.GroupId == r.GroupId)
            .OrderByDescending(m => m.UserId == group.HostUserId).ThenBy(m => m.UserId)
            .Select(m => new ChatGroupMemberDto(m.UserId, m.User.FullName, m.User.AvatarUrl, m.UserId == group.HostUserId,
                !group.IsClosed && m.User.Status == AccountStatus.Active && m.UserId != group.HostUserId)).ToListAsync(ct);
        return Result.Success(new ChatGroupMembersDto(group.Id, group.GroupName, members.Count, members, group.IsClosed));
    }

    public Task<Result<ChatGroupConversationDto>> Handle(RenameChatGroup r, CancellationToken ct) =>
        InGroupTransaction(r.GroupId, async token =>
        {
            if (!await Active(r.ActorId, token)) return Forbidden<ChatGroupConversationDto>();
            var group = await Accessible(r.ActorId).SingleOrDefaultAsync(g => g.Id == r.GroupId, token);
            if (group is null) return Missing<ChatGroupConversationDto>();
            if (group.HostUserId != r.ActorId) return HostRequired<ChatGroupConversationDto>();
            if (group.IsClosed) return Closed<ChatGroupConversationDto>();
            var name = r.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > ChatGroup.MaxNameLength) return Invalid<ChatGroupConversationDto>();
            group.Rename(r.ActorId, name);
            await db.SaveChangesAsync(token);
            return Result.Success(await Project(Accessible(r.ActorId).Where(g => g.Id == r.GroupId), r.ActorId).SingleAsync(token));
        }, ct);

    public Task<Result<bool>> Handle(KickChatGroupMember r, CancellationToken ct) =>
        InGroupTransaction(r.GroupId, async token =>
        {
            if (!await Active(r.ActorId, token)) return Forbidden<bool>();
            var group = await Accessible(r.ActorId).Include(g => g.Members).SingleOrDefaultAsync(g => g.Id == r.GroupId, token);
            if (group is null) return Missing<bool>();
            if (group.HostUserId != r.ActorId) return HostRequired<bool>();
            if (group.IsClosed) return Closed<bool>();
            if (r.UserId <= 0 || r.UserId == r.ActorId || r.UserId == group.HostUserId) return Invalid<bool>();
            var member = group.RemoveMember(r.ActorId, r.UserId);
            if (member is not null)
            {
                db.ChatGroupMembers.Remove(member);
                await db.SaveChangesAsync(token);
            }
            return Result.Success(member is not null);
        }, ct);

    public Task<Result<bool>> Handle(DissolveChatGroup r, CancellationToken ct) =>
        InGroupTransaction(r.GroupId, async token =>
        {
            if (!await Active(r.ActorId, token)) return Forbidden<bool>();
            var group = await Accessible(r.ActorId).SingleOrDefaultAsync(g => g.Id == r.GroupId, token);
            if (group is null) return Missing<bool>();
            if (group.HostUserId != r.ActorId) return HostRequired<bool>();
            if (!group.IsClosed)
            {
                group.Dissolve(r.ActorId);
                await db.SaveChangesAsync(token);
            }
            return Result.Success(true);
        }, ct);

    // All independent group access shares the membership/closure boundary. A nested share owns rollback/retry.
    private async Task<Result<T>> InGroupTransaction<T>(long groupId,
        Func<CancellationToken, Task<Result<T>>> operation, CancellationToken ct)
    {
        if (groupId <= 0) return Invalid<T>();
        async Task<Result<T>> Locked(CancellationToken token)
        {
            await db.AcquireChatGroupLockAsync(groupId, token);
            return await operation(token);
        }
        if (db.HasActiveTransaction) return await Locked(ct);
        for (var attempt = 0; ; attempt++)
        {
            try { return await db.ExecuteInSerializableResultTransactionAsync(Locked, ct); }
            catch (Exception ex) when (db.IsDeadlock(ex) || ex is DbUpdateException update &&
                (update is DbUpdateConcurrencyException || GroupChatPersistence.IsUniqueConflict(update, ChatGroupLeaveOperation.RetryIndex)
                    || GroupChatPersistence.IsUniqueConflict(update, "UX_ChatGroupMessages_ClientKey")))
            {
                db.ClearTrackedEntities();
                ct.ThrowIfCancellationRequested();
                if (attempt >= 3) return Result.Failure<T>(ChatGroupErrors.Conflict,
                    "The group changed concurrently. Retry with the same idempotency key.");
                await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), ct);
            }
        }
    }

    public Task<Result<ChatGroupLeaveReceipt>> Handle(LeaveChatGroup r, CancellationToken ct)
    {
        if (r.IdempotencyKey == Guid.Empty)
            return Task.FromResult(Invalid<ChatGroupLeaveReceipt>());
        return InGroupTransaction(r.GroupId, async token =>
        {
            if (!await Active(r.ActorId, token)) return Forbidden<ChatGroupLeaveReceipt>();
            var previous = await db.ChatGroupLeaveOperations.AsNoTracking().SingleOrDefaultAsync(o =>
                o.GroupId == r.GroupId && o.ActorId == r.ActorId && o.ClientRequestId == r.IdempotencyKey, token);
            if (previous is not null)
                return previous.ReplacementHostUserId != r.ReplacementHostUserId
                    ? Result.Failure<ChatGroupLeaveReceipt>(ChatGroupErrors.Conflict, "Idempotency-Key was already used with a different replacement host.")
                    : Result.Success(Receipt(previous));
            var group = await Accessible(r.ActorId).Include(g => g.Members).SingleOrDefaultAsync(g => g.Id == r.GroupId, token);
            if (group is null) return Missing<ChatGroupLeaveReceipt>();
            if (group.IsClosed) return Closed<ChatGroupLeaveReceipt>();
            if (group.HostUserId == r.ActorId)
            {
                var eligible = await db.ChatGroupMembers.Where(m => m.GroupId == r.GroupId && m.UserId != r.ActorId
                    && m.User.Status == AccountStatus.Active).Select(m => m.UserId).ToListAsync(token);
                if (eligible.Count == 0)
                    return Result.Failure<ChatGroupLeaveReceipt>(ChatGroupErrors.Conflict,
                        "Add an Active member to the group, then explicitly select that member as replacement host before leaving.");
                if (r.ReplacementHostUserId is not { } replacement || !eligible.Contains(replacement))
                    return Result.Failure<ChatGroupLeaveReceipt>(ChatGroupErrors.Conflict,
                        "Explicitly select an existing Active member other than yourself as replacement host before leaving.");
                group.TransferHost(r.ActorId, replacement);
            }
            else if (r.ReplacementHostUserId is not null)
                return Result.Failure<ChatGroupLeaveReceipt>(ChatGroupErrors.Invalid, "Only the current host may select a replacement host.");
            var member = group.Leave(r.ActorId);
            var receipt = new ChatGroupLeaveOperation(group, r.ActorId, r.IdempotencyKey, r.ReplacementHostUserId, clock.UtcNow);
            db.ChatGroupLeaveOperations.Add(receipt);
            db.ChatGroupMembers.Remove(member);
            await db.SaveChangesAsync(token);
            return Result.Success(Receipt(receipt));
        }, ct);
    }

    private static ChatGroupLeaveReceipt Receipt(ChatGroupLeaveOperation operation) => new(operation.GroupId,
        ChatGroupLeaveOperation.LeftStatus, operation.NewHostUserId, operation.LeftAtUtc);

}