using MediatR;

using TripMate.Application.Common.Models;
namespace TripMate.Application.Features.TravelGroups.Chat;

public sealed record EditGroupMessage(long GroupId, long MessageId, long ActorId, string? Content,
    string? MetadataJson, long ExpectedRevision, Guid ClientRequestId) : IRequest<Result<GroupMessageDto>>;
public sealed record GetImportantGroupMessages(long GroupId, long ActorId, int Limit = 20, long? BeforeMessageId = null)
    : IRequest<Result<ImportantGroupMessagePage>>;
public sealed record ImportantGroupMessagePage(IReadOnlyList<GroupMessageDto> Items, long? NextBeforeMessageId);
public sealed record GetGroupPollVotes(long GroupId, long MessageId, long ActorId) : IRequest<Result<IReadOnlyList<GroupPollVoterDto>>>;
public sealed record GroupPollVoterDto(long UserId, string DisplayName, string? AvatarUrl, int OptionIndex, DateTimeOffset VotedAtUtc);