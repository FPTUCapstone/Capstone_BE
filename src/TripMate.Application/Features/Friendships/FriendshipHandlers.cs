using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Friendships;

public sealed class FriendshipHandlers(IApplicationDbContext db, IDateTimeProvider clock) :
    IRequestHandler<ListFriendships, Result<IReadOnlyList<FriendshipDto>>>,
    IRequestHandler<UpdateFriendship, Result>
{
    private Task<bool> Active(long id, CancellationToken ct) => db.Users.AnyAsync(
        u => u.Id == id && u.Status == AccountStatus.Active, ct);

    public async Task<Result<IReadOnlyList<FriendshipDto>>> Handle(ListFriendships r, CancellationToken ct)
    {
        if (!await Active(r.ActorId, ct))
            return Result.Failure<IReadOnlyList<FriendshipDto>>(FriendshipErrors.Forbidden, "An Active account is required.");
        var items = await db.Friendships.AsNoTracking()
            .Where(f => (f.LowerUserId == r.ActorId || f.UpperUserId == r.ActorId)
                && f.LowerUser.Status == AccountStatus.Active && f.UpperUser.Status == AccountStatus.Active)
            .OrderBy(f => f.LowerUserId == r.ActorId ? f.UpperUser.FullName : f.LowerUser.FullName)
            .ThenBy(f => f.LowerUserId == r.ActorId ? f.UpperUserId : f.LowerUserId)
            .Select(f => new FriendshipDto(f.LowerUserId == r.ActorId ? f.UpperUserId : f.LowerUserId,
                f.LowerUserId == r.ActorId ? f.UpperUser.FullName : f.LowerUser.FullName,
                f.LowerUserId == r.ActorId ? f.UpperUser.AvatarUrl : f.LowerUser.AvatarUrl,
                f.Status, f.RequestedByUserId)).ToListAsync(ct);
        return Result.Success<IReadOnlyList<FriendshipDto>>(items);
    }

    public async Task<Result> Handle(UpdateFriendship r, CancellationToken ct)
    {
        if (r.OtherUserId <= 0 || r.OtherUserId == r.ActorId || !Friendship.IsAction(r.Action))
            return Result.Failure(FriendshipErrors.Invalid, "A different positive user ID and a supported action are required.");
        var lower = Math.Min(r.ActorId, r.OtherUserId);
        var upper = Math.Max(r.ActorId, r.OtherUserId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await db.ExecuteInSerializableTransactionAsync(async token =>
                {
                    // Execution strategies may replay the delegate. Never reuse stale tracked state.
                    db.ClearTrackedEntities();
                    if (!await Active(r.ActorId, token))
                        return Result.Failure(FriendshipErrors.Forbidden, "An Active account is required.");
                    if (!await Active(r.OtherUserId, token))
                        return Result.Failure(FriendshipErrors.NotFound, "Active user not found.");
                    var friendship = await db.Friendships.SingleOrDefaultAsync(
                        f => f.LowerUserId == lower && f.UpperUserId == upper, token);
                    if (friendship is null)
                    {
                        if (r.Action != Friendship.Request)
                            return Result.Failure(FriendshipErrors.Conflict, "No friendship request or friendship exists.");
                        db.Friendships.Add(new Friendship(r.ActorId, r.OtherUserId, clock.UtcNow));
                    }
                    else
                    {
                        var transition = friendship.Apply(r.ActorId, r.Action!);
                        if (transition == FriendshipTransition.Forbidden)
                            return Result.Failure(FriendshipErrors.Forbidden, "This action is not allowed for this participant.");
                        if (transition == FriendshipTransition.Conflict)
                            return Result.Failure(FriendshipErrors.Conflict, "This action is not allowed in the current friendship state.");
                        if (transition == FriendshipTransition.Delete) db.Friendships.Remove(friendship);
                    }
                    await db.SaveChangesAsync(token);
                    return Result.Success();
                }, ct);
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException || db.IsDeadlock(ex)
                || ex is DbUpdateException update && db.IsFriendshipPairUniqueConflict(update))
            {
                db.ClearTrackedEntities();
                if (attempt < 2)
                    await Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1)), ct);
            }
        }
        return Result.Failure(FriendshipErrors.Conflict, "Friendship changed concurrently. Retry the request.");
    }
}