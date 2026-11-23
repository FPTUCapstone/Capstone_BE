using MediatR;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.Chat.VotePoll;

public sealed record VotePollCommand(long MessageId, long UserId, int OptionIndex)
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
            .Include(m => m.PollVotes)
            .FirstOrDefaultAsync(m => m.Id == request.MessageId, cancellationToken);

        if (message is null)
        {
            return Result.Failure<IReadOnlyDictionary<int, int>>(
                TravelGroupErrorCodes.MessageNotFound, "Message not found.");
        }

        var isMember = await dbContext.GroupMembers
            .AsNoTracking()
            .AnyAsync(m => m.GroupId == message.GroupId
                && m.UserId == request.UserId
                && m.Status == GroupMemberStatus.Active,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure<IReadOnlyDictionary<int, int>>(
                TravelGroupErrorCodes.ActiveMembershipRequired, "Active membership is required.");
        }

        message.RecordPollVote(request.UserId, request.OptionIndex, dateTimeProvider.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        var counts = message.PollVotes
            .GroupBy(v => v.OptionIndex)
            .ToDictionary(g => g.Key, g => g.Count());

        return Result.Success<IReadOnlyDictionary<int, int>>(counts);
    }
}
