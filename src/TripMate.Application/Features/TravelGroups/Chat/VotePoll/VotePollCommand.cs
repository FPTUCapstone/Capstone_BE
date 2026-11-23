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
        if (request.OptionIndex < 0 || request.OptionIndex > 5)
        {
            return Result.Failure<IReadOnlyDictionary<int, int>>(
                TravelGroupErrorCodes.InvalidPollOptions, "Option index must be between 0 and 5.");
        }

        var message = await dbContext.GroupMessages
            .Include(m => m.Group)
            .Include(m => m.PollVotes)
            .FirstOrDefaultAsync(
                m => m.Id == request.MessageId && m.GroupId == request.GroupId,
                cancellationToken);

        if (message is null)
        {
            return Result.Failure<IReadOnlyDictionary<int, int>>(
                TravelGroupErrorCodes.MessageNotFound, "Message not found.");
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

        if (message.Group.Status != TravelGroupStatus.Active)
        {
            return Result.Failure<IReadOnlyDictionary<int, int>>(
                TravelGroupErrorCodes.GroupClosed, "Cannot vote on polls in a closed group.");
        }

        if (member.Status != GroupMemberStatus.Active)
        {
            return Result.Failure<IReadOnlyDictionary<int, int>>(
                TravelGroupErrorCodes.ActiveMembershipRequired, "Active membership is required.");
        }

        if (!string.Equals(message.MessageType, "poll", StringComparison.Ordinal))
        {
            return Result.Failure<IReadOnlyDictionary<int, int>>(
                TravelGroupErrorCodes.InvalidMessageType, "Only poll messages support voting.");
        }

        message.RecordPollVote(request.UserId, request.OptionIndex, dateTimeProvider.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        var counts = message.PollVotes
            .GroupBy(v => v.OptionIndex)
            .ToDictionary(g => g.Key, g => g.Count());

        return Result.Success<IReadOnlyDictionary<int, int>>(counts);
    }
}