using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.ChatGroups;
using TripMate.Application.Features.DirectChat;
using TripMate.Application.Features.TravelGroups.Chat;
using TripMate.Application.Features.TravelGroups.Chat.SendGroupMessage;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.ItineraryShares;

public sealed class ItineraryShareHandlers(IApplicationDbContext db, IDateTimeProvider clock, ISender sender) :
    IRequestHandler<ShareItinerary, Result<ItineraryShareSent>>,
    IRequestHandler<GetItineraryShare, Result<ItineraryShareDto>>
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);
    private static Result<T> Missing<T>() => Result.Failure<T>(ItineraryShareErrors.NotFound, "Itinerary share or source not found.");
    private Task<bool> Active(long actor, CancellationToken ct) => db.Users.AsNoTracking()
        .AnyAsync(u => u.Id == actor && u.Status == AccountStatus.Active, ct);

    private Task<bool> CanAccess(string kind, long id, long actor, bool sending, CancellationToken ct) => kind switch
    {
        ItineraryShare.TravelGroup => db.GroupMembers.AsNoTracking().AnyAsync(m => m.GroupId == id
            && m.UserId == actor && m.Status == GroupMemberStatus.Active
            && (m.User.Role == UserRole.Traveler || m.User.Role == UserRole.TourOperator)
            && (!sending || m.TravelGroup.Status == TravelGroupStatus.Active), ct),
        ItineraryShare.ChatGroup => db.ChatGroups.AsNoTracking().AnyAsync(g => g.Id == id
            && g.Members.Any(m => m.UserId == actor) && (!sending || !g.IsClosed), ct),
        ItineraryShare.Direct => db.DirectConversations.AsNoTracking().AnyAsync(c => c.Id == id
            && (c.LowerUserId == actor || c.UpperUserId == actor)
            && c.LowerUser.Status == AccountStatus.Active && c.UpperUser.Status == AccountStatus.Active, ct),
        _ => Task.FromResult(false)
    };

    public async Task<Result<ItineraryShareSent>> Handle(ShareItinerary r, CancellationToken ct)
    {
        if (r.ActorId <= 0 || r.ItineraryId <= 0 || r.ConversationId <= 0 || r.ClientRequestId == Guid.Empty
            || r.ConversationKind is not (ItineraryShare.TravelGroup or ItineraryShare.ChatGroup or ItineraryShare.Direct))
            return Result.Failure<ItineraryShareSent>(ItineraryShareErrors.Invalid, "Positive IDs, a nonempty GUID and an exact conversation kind are required.");

        // This key belongs to the share boundary. The existing send handler uses ShareId as its
        // message retry key, so ordinary text requests cannot accidentally consume ClientRequestId.
        var shareId = Guid.NewGuid();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await db.ExecuteInSerializableResultTransactionAsync(async token =>
                {
                    if (!await Active(r.ActorId, token))
                        return Result.Failure<ItineraryShareSent>(ItineraryShareErrors.Forbidden, "An Active account is required.");
                    var existing = await db.ItineraryShares.AsNoTracking().SingleOrDefaultAsync(s =>
                        s.ActorId == r.ActorId && s.ClientRequestId == r.ClientRequestId, token);
                    if (existing is not null && (existing.ItineraryId != r.ItineraryId
                        || existing.ConversationKind != r.ConversationKind || existing.ConversationId != r.ConversationId))
                        return Result.Failure<ItineraryShareSent>(ItineraryShareErrors.Conflict, "Client request ID was already used with a different payload.");
                    if (!await CanAccess(r.ConversationKind!, r.ConversationId, r.ActorId, true, token))
                        return Missing<ItineraryShareSent>();
                    if (existing is not null)
                        return Result.Success(new ItineraryShareSent(new(existing.ShareId, existing.MessageId), false, null));

                    // Project only the owner header and the public POI/timeline whitelist. No entity
                    // serialization and no booking, payment, customer, cost or note joins.
                    var header = await db.Itineraries.Where(i => i.Id == r.ItineraryId && i.TravelerUserId == r.ActorId)
                        .Select(i => new { i.Title, i.Version }).SingleOrDefaultAsync(token);
                    if (header is null) return Missing<ItineraryShareSent>();
                    var items = await db.ItineraryItems.Where(i => i.ItineraryId == r.ItineraryId)
                        .OrderBy(i => i.SequenceNo).ThenBy(i => i.Id)
                        .Select(i => new ItineraryShareItemDto(i.SequenceNo,
                            i.PointOfInterest != null ? i.PointOfInterest.Name : "Rest stop",
                            i.PlannedArrivalUtc, i.PlannedDepartureUtc, i.StayDurationMinutes,
                            i.PointOfInterest != null ? (decimal?)i.PointOfInterest.Latitude : null,
                            i.PointOfInterest != null ? (decimal?)i.PointOfInterest.Longitude : null,
                            i.PointOfInterest != null ? i.PointOfInterest.Address : null)).ToListAsync(token);
                    var sharedAt = clock.UtcNow;
                    var content = ItineraryShare.MessagePrefix + shareId.ToString("D");
                    long messageId;
                    GroupMessageDto? travelMessage = null;
                    // Existing send handlers persist within this transaction; they do not notify.
                    switch (r.ConversationKind)
                    {
                        case ItineraryShare.TravelGroup:
                            var travel = await sender.Send(new SendGroupMessageCommand(r.ConversationId,
                                r.ActorId, GroupMessage.TextType, content, ClientMessageId: shareId), token);
                            if (travel.IsFailure) return SendFailure();
                            travelMessage = travel.Value;
                            messageId = travel.Value.MessageId;
                            break;
                        case ItineraryShare.ChatGroup:
                            var group = await sender.Send(new SendChatGroupMessage(r.ActorId, r.ConversationId, content, shareId), token);
                            if (group.IsFailure) return SendFailure();
                            messageId = group.Value.MessageId;
                            break;
                        default:
                            var direct = await sender.Send(new SendDirectMessage(r.ActorId, r.ConversationId, content, shareId), token);
                            if (direct.IsFailure) return SendFailure();
                            messageId = direct.Value.MessageId;
                            break;
                    }
                    var snapshot = new ItineraryShare(shareId, r.ActorId, r.ClientRequestId, r.ItineraryId,
                        r.ConversationKind!, r.ConversationId, messageId, header.Title ?? ItineraryShare.DefaultTitle,
                        sharedAt, header.Version, JsonSerializer.Serialize(items, SnapshotJson));
                    db.ItineraryShares.Add(snapshot);
                    await db.SaveChangesAsync(token);
                    return Result.Success(new ItineraryShareSent(new(shareId, messageId), true, travelMessage));
                }, ct);
            }
            catch (Exception ex) when (db.IsDeadlock(ex)
                || ex is DbUpdateException update && (update is DbUpdateConcurrencyException
                    || GroupChatPersistence.IsUniqueConflict(update, ItineraryShare.RetryIndex)))
            {
                // The outer transaction has disposed/rolled back before adopting a durable winner.
                db.ClearTrackedEntities();
                ct.ThrowIfCancellationRequested();
                if (attempt >= 3) return Result.Failure<ItineraryShareSent>(ItineraryShareErrors.Conflict,
                    "Concurrent share request. Retry with the same clientRequestId.");
                await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), ct);
            }
        }
    }

    private static Result<ItineraryShareSent> SendFailure() =>
        Result.Failure<ItineraryShareSent>(ItineraryShareErrors.Conflict,
            "The chat message could not be sent. Retry with the same clientRequestId.");

    public Task<Result<ItineraryShareDto>> Handle(GetItineraryShare r, CancellationToken ct) =>
        db.ExecuteInSerializableResultTransactionAsync(async token =>
        {
            if (r.ShareId == Guid.Empty || !await Active(r.ActorId, token)) return Missing<ItineraryShareDto>();
            var snapshot = await db.ItineraryShares.AsNoTracking().SingleOrDefaultAsync(s => s.ShareId == r.ShareId, token);
            if (snapshot is null || !await CanAccess(snapshot.ConversationKind, snapshot.ConversationId, r.ActorId, false, token))
                return Missing<ItineraryShareDto>();
            // A share is visible only through its original message's history, never through a
            // guessed ID or a fabricated chat group sharing another conversation's numeric ID.
            var inHistory = snapshot.ConversationKind switch
            {
                ItineraryShare.TravelGroup => await db.GroupMessages.AnyAsync(m => m.Id == snapshot.MessageId
                    && m.GroupId == snapshot.ConversationId && m.SenderUserId == snapshot.ActorId, token),
                ItineraryShare.ChatGroup => await db.ChatGroupMessages.AnyAsync(m => m.Id == snapshot.MessageId
                    && m.GroupId == snapshot.ConversationId && m.SenderUserId == snapshot.ActorId, token),
                ItineraryShare.Direct => await db.DirectMessages.AnyAsync(m => m.Id == snapshot.MessageId
                    && m.ConversationId == snapshot.ConversationId && m.SenderUserId == snapshot.ActorId, token),
                _ => false
            };
            if (!inHistory) return Missing<ItineraryShareDto>();
            return Result.Success(new ItineraryShareDto(snapshot.ShareId, snapshot.Title, snapshot.SharedAtUtc,
                snapshot.ItineraryVersion, JsonSerializer.Deserialize<ItineraryShareItemDto[]>(snapshot.ItemsJson, SnapshotJson)!));
        }, ct);
}