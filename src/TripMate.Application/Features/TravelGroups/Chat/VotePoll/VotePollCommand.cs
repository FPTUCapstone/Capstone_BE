using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.Chat.VotePoll;

public sealed record VotePollCommand(long GroupId, long MessageId, long UserId, int OptionIndex)
    : IRequest<Result<IReadOnlyDictionary<int, int>>>;

public sealed class VotePollCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<VotePollCommand, Result<IReadOnlyDictionary<int, int>>>
{
    public async Task<Result<IReadOnlyDictionary<int, int>>> Handle(
        VotePollCommand request,
        CancellationToken cancellationToken)
    {
        var group = await dbContext.TravelGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == request.GroupId, cancellationToken);

        if (group is null)
        {
            return Result.Failure<IReadOnlyDictionary<int, int>>(
                TravelGroupErrorCodes.GroupNotFound, "Group not found.");
        }

        var member = await dbContext.GroupMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.GroupId == request.GroupId && m.UserId == request.UserId,
                cancellationToken);

        if (member is null || member.Status == GroupMemberStatus.Removed)
        {
            return Result.Failure<IReadOnlyDictionary<int, int>>(
                TravelGroupErrorCodes.ActiveMembershipRequired, "Active membership is required.");
        }

        if (group.Status != TravelGroupStatus.Active)
        {
            return Result.Failure<IReadOnlyDictionary<int, int>>(
                TravelGroupErrorCodes.GroupClosed, "Cannot vote on polls in a closed group.");
        }

        if (member.Status != GroupMemberStatus.Active)
        {
            return Result.Failure<IReadOnlyDictionary<int, int>>(
                TravelGroupErrorCodes.ActiveMembershipRequired, "Active membership is required.");
        }

        var message = await dbContext.GroupMessages
            .Include(m => m.PollVotes)
            .FirstOrDefaultAsync(
                m => m.Id == request.MessageId && m.GroupId == request.GroupId,
                cancellationToken);

        if (message is null)
        {
            return Result.Failure<IReadOnlyDictionary<int, int>>(
                TravelGroupErrorCodes.MessageNotFound, "Message not found.");
        }

        if (!string.Equals(message.MessageType, "poll", StringComparison.Ordinal))
        {
            return Result.Failure<IReadOnlyDictionary<int, int>>(
                TravelGroupErrorCodes.InvalidMessageType, "Only poll messages support voting.");
        }

        if (!GroupChatMetadata.TryGetPollOptionCount(message.MetadataJson, out var optionCount)
            || request.OptionIndex < -1 || request.OptionIndex >= optionCount)
        {
            return Result.Failure<IReadOnlyDictionary<int, int>>(
                TravelGroupErrorCodes.InvalidPollOptions, "Select an existing poll option, or -1 to cancel.");
        }

        if (request.OptionIndex == -1)
        {
            var vote = message.PollVotes.FirstOrDefault(v => v.UserId == request.UserId);
            if (vote is not null)
            {
                dbContext.GroupPollVotes.Remove(vote);
                try { await dbContext.SaveChangesAsync(cancellationToken); }
                catch (DbUpdateConcurrencyException ex) when (ex.Entries.Count == 1
                    && ReferenceEquals(ex.Entries[0].Entity, vote)
                    && ex.Entries[0].State == EntityState.Deleted)
                {
                    // Another cancellation may have deleted this exact vote. Other conflicts must propagate.
                    if (await dbContext.GroupPollVotes.AsNoTracking().AnyAsync(v => v.Id == vote.Id, cancellationToken))
                        throw;
                    ex.Entries[0].State = EntityState.Detached;
                }
            }
        }
        else
        {
            message.RecordPollVote(request.UserId, request.OptionIndex, dateTimeProvider.UtcNow);
            await GroupChatPersistence.SaveInteractionAsync(dbContext, "UQ_GroupPollVotes_MessageUser", async () =>
            {
                var winner = await dbContext.GroupPollVotes.SingleAsync(
                    row => row.MessageId == request.MessageId && row.UserId == request.UserId, cancellationToken);
                winner.UpdateOption(request.OptionIndex, dateTimeProvider.UtcNow);
                await dbContext.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
        }

        var counts = (await dbContext.GroupPollVotes.AsNoTracking()
            .Where(v => v.MessageId == message.Id).ToListAsync(cancellationToken))
            .GroupBy(v => v.OptionIndex)
            .ToDictionary(g => g.Key, g => g.Count());

        return Result.Success<IReadOnlyDictionary<int, int>>(counts);
    }
}