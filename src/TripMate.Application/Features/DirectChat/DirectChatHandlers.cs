using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.DirectChat;

public sealed class DirectChatHandlers(IApplicationDbContext db) :
    IRequestHandler<SearchDirectUsers, Result<IReadOnlyList<DirectUserDto>>>,
    IRequestHandler<StartDirectConversation, Result<DirectConversationDto>>,
    IRequestHandler<ListDirectConversations, Result<DirectConversationPage>>,
    IRequestHandler<GetDirectMessages, Result<IReadOnlyList<DirectMessageDto>>>,
    IRequestHandler<SendDirectMessage, Result<DirectMessageDto>>,
    IRequestHandler<ReadDirectConversation, Result>
{
    private Task<bool> Active(long id, CancellationToken ct) => db.Users.AsNoTracking()
        .AnyAsync(u => u.Id == id && u.Status == AccountStatus.Active, ct);

    private IQueryable<DirectConversation> Accessible(long actorId) => db.DirectConversations.Where(c =>
        (c.LowerUserId == actorId || c.UpperUserId == actorId)
        && c.LowerUser.Status == AccountStatus.Active && c.UpperUser.Status == AccountStatus.Active);

    private static Result<T> Invalid<T>() => Result.Failure<T>(DirectChatErrors.Invalid, "Invalid direct chat request.");
    private static Result<T> Missing<T>() => Result.Failure<T>(DirectChatErrors.NotFound, "Conversation not found.");
    private static Result<T> Forbidden<T>() => Result.Failure<T>(DirectChatErrors.Forbidden, "An Active account is required.");
    private static bool ValidPage(int limit, long? before) => limit is >= 1 and <= 100 && (before is null || before > 0);

    public async Task<Result<IReadOnlyList<DirectUserDto>>> Handle(SearchDirectUsers r, CancellationToken ct)
    {
        if (!await Active(r.ActorId, ct)) return Forbidden<IReadOnlyList<DirectUserDto>>();
        var search = r.Search?.Trim();
        if (search is null || search.Length is < 2 or > 100 || !ValidPage(r.Limit, null)) return Invalid<IReadOnlyList<DirectUserDto>>();
        var users = await db.Users.AsNoTracking().Where(u => u.Status == AccountStatus.Active
                && u.Id != r.ActorId && u.FullName.Contains(search))
            .OrderBy(u => u.FullName).ThenBy(u => u.Id).Take(r.Limit)
            .Select(u => new DirectUserDto(u.Id, u.FullName, u.AvatarUrl)).ToListAsync(ct);
        return Result.Success<IReadOnlyList<DirectUserDto>>(users);
    }

    public async Task<Result<DirectConversationDto>> Handle(StartDirectConversation r, CancellationToken ct)
    {
        if (!await Active(r.ActorId, ct)) return Forbidden<DirectConversationDto>();
        if (r.OtherUserId <= 0 || r.OtherUserId == r.ActorId) return Invalid<DirectConversationDto>();
        if (!await Active(r.OtherUserId, ct)) return Missing<DirectConversationDto>();
        var lower = Math.Min(r.ActorId, r.OtherUserId);
        var upper = Math.Max(r.ActorId, r.OtherUserId);
        var conversation = await db.DirectConversations.FirstOrDefaultAsync(c => c.LowerUserId == lower && c.UpperUserId == upper, ct);
        if (conversation is null)
        {
            conversation = new DirectConversation(lower, upper, DateTimeOffset.UtcNow);
            db.DirectConversations.Add(conversation);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                // The unique ordered pair is the arbiter across processes. Adopt only a committed winner.
                db.ClearTrackedEntities();
                conversation = await db.DirectConversations.FirstOrDefaultAsync(c => c.LowerUserId == lower && c.UpperUserId == upper, ct);
                if (conversation is null) throw;
            }
        }
        var dto = await Project(Accessible(r.ActorId).Where(c => c.Id == conversation.Id), r.ActorId).FirstOrDefaultAsync(ct);
        return dto is null ? Missing<DirectConversationDto>() : Result.Success(dto);
    }

    private IQueryable<DirectConversationDto> Project(IQueryable<DirectConversation> conversations, long actorId) => conversations.AsNoTracking().Select(c =>
        new DirectConversationDto(c.Id, c.LowerUserId == actorId ? c.UpperUserId : c.LowerUserId,
            c.LowerUserId == actorId ? c.UpperUser.FullName : c.LowerUser.FullName,
            c.LowerUserId == actorId ? c.UpperUser.AvatarUrl : c.LowerUser.AvatarUrl,
            db.DirectMessages.Where(m => m.ConversationId == c.Id).OrderByDescending(m => m.Id).Select(m => m.Content).FirstOrDefault(),
            db.DirectMessages.Where(m => m.ConversationId == c.Id).OrderByDescending(m => m.Id).Select(m => (DateTimeOffset?)m.CreatedAtUtc).FirstOrDefault(),
            db.DirectMessages.Count(m => m.ConversationId == c.Id && m.SenderUserId != actorId && m.Id > (c.LowerUserId == actorId ? c.LowerReadThroughMessageId : c.UpperReadThroughMessageId))));

    public async Task<Result<DirectConversationPage>> Handle(ListDirectConversations r, CancellationToken ct)
    {
        if (!await Active(r.ActorId, ct)) return Forbidden<DirectConversationPage>();
        if (!ValidPage(r.Limit, r.BeforeConversationId)) return Invalid<DirectConversationPage>();
        var conversations = Accessible(r.ActorId).Where(c => r.BeforeConversationId == null || c.Id < r.BeforeConversationId)
            .OrderByDescending(c => c.Id).Take(r.Limit + 1);
        var items = await Project(conversations, r.ActorId).ToListAsync(ct);
        var more = items.Count > r.Limit;
        if (more) items.RemoveAt(items.Count - 1);
        return Result.Success(new DirectConversationPage(items, more ? items[^1].ConversationId : null));
    }

    private static DirectMessageDto ToDto(DirectMessage m, string name) => new(m.Id, m.ConversationId,
        m.SenderUserId, name, m.Content, m.CreatedAtUtc, m.ClientMessageId.ToString("D"),
        m.Id <= (m.SenderUserId == m.Conversation.LowerUserId ? m.Conversation.UpperReadThroughMessageId : m.Conversation.LowerReadThroughMessageId));

    public async Task<Result<IReadOnlyList<DirectMessageDto>>> Handle(GetDirectMessages r, CancellationToken ct)
    {
        if (!await Active(r.ActorId, ct)) return Forbidden<IReadOnlyList<DirectMessageDto>>();
        if (r.ConversationId <= 0 || !ValidPage(r.Limit, r.BeforeMessageId)) return Invalid<IReadOnlyList<DirectMessageDto>>();
        if (!await Accessible(r.ActorId).AnyAsync(c => c.Id == r.ConversationId, ct)) return Missing<IReadOnlyList<DirectMessageDto>>();
        var messages = await db.DirectMessages.AsNoTracking().Include(m => m.SenderUser).Include(m => m.Conversation)
            .Where(m => m.ConversationId == r.ConversationId && (r.BeforeMessageId == null || m.Id < r.BeforeMessageId))
            .OrderByDescending(m => m.Id).Take(r.Limit).ToListAsync(ct);
        return Result.Success<IReadOnlyList<DirectMessageDto>>(messages.OrderBy(m => m.Id).Select(m => ToDto(m, m.SenderUser.FullName)).ToArray());
    }

    public async Task<Result<DirectMessageDto>> Handle(SendDirectMessage r, CancellationToken ct)
    {
        if (!await Active(r.ActorId, ct)) return Forbidden<DirectMessageDto>();
        var content = r.Content?.Trim();
        if (r.ConversationId <= 0 || string.IsNullOrEmpty(content) || content.Length > DirectMessage.MaxContentLength || r.ClientMessageId == Guid.Empty)
            return Invalid<DirectMessageDto>();
        var conversation = await Accessible(r.ActorId).FirstOrDefaultAsync(c => c.Id == r.ConversationId, ct);
        if (conversation is null) return Missing<DirectMessageDto>();
        var message = await db.DirectMessages.FirstOrDefaultAsync(m => m.ConversationId == r.ConversationId
            && m.SenderUserId == r.ActorId && m.ClientMessageId == r.ClientMessageId, ct);
        if (message is null)
        {
            message = new DirectMessage(conversation, r.ActorId, content, r.ClientMessageId, DateTimeOffset.UtcNow);
            db.DirectMessages.Add(message);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                db.ClearTrackedEntities();
                message = await db.DirectMessages.FirstOrDefaultAsync(m => m.ConversationId == r.ConversationId
                    && m.SenderUserId == r.ActorId && m.ClientMessageId == r.ClientMessageId, ct);
                if (message is null) throw;
            }
        }
        if (!string.Equals(message.Content, content, StringComparison.Ordinal))
            return Result.Failure<DirectMessageDto>(DirectChatErrors.Conflict, "Client message ID was already used with different content.");
        message = await db.DirectMessages.Include(m => m.Conversation).FirstAsync(m => m.Id == message.Id, ct);
        // Revalidate after persistence, including on retries.
        if (!await Accessible(r.ActorId).AnyAsync(c => c.Id == r.ConversationId, ct)) return Missing<DirectMessageDto>();
        var name = await db.Users.Where(u => u.Id == r.ActorId).Select(u => u.FullName).SingleAsync(ct);
        return Result.Success(ToDto(message, name));
    }

    public async Task<Result> Handle(ReadDirectConversation r, CancellationToken ct)
    {
        if (!await Active(r.ActorId, ct)) return Result.Failure(DirectChatErrors.Forbidden, "An Active account is required.");
        if (r.ConversationId <= 0 || r.ThroughMessageId <= 0) return Result.Failure(DirectChatErrors.Invalid, "Positive conversation and message IDs are required.");
        if (!await Accessible(r.ActorId).AnyAsync(c => c.Id == r.ConversationId, ct)) return Result.Failure(DirectChatErrors.NotFound, "Conversation not found.");
        if (!await db.DirectMessages.AnyAsync(m => m.ConversationId == r.ConversationId && m.Id == r.ThroughMessageId, ct))
            return Result.Failure(DirectChatErrors.Invalid, "Through message must belong to this conversation.");
        // Optimistic concurrency makes two devices' cursors monotonic even when saves race.
        for (var attempt = 0; ; attempt++)
        {
            var conversation = await Accessible(r.ActorId).FirstOrDefaultAsync(c => c.Id == r.ConversationId, ct);
            if (conversation is null) return Result.Failure(DirectChatErrors.NotFound, "Conversation not found.");
            conversation.MarkRead(r.ActorId, r.ThroughMessageId);
            try
            {
                await db.SaveChangesAsync(ct);
                return Result.Success();
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                db.ClearTrackedEntities();
            }
        }
    }
}