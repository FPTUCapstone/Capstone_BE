namespace TripMate.Application.Features.TravelGroups.Chat;

public sealed record GroupMessageDto(
    long MessageId,
    long GroupId,
    long SenderUserId,
    string SenderName,
    string? SenderAvatarUrl,
    string MessageType,
    string Content,
    string? MetadataJson,
    DateTimeOffset CreatedAtUtc,
    int ReadByCount,
    bool IsReadByMe,
    int PresentCount,
    bool IsPresentByMe,
    IReadOnlyList<long> PaidUserIds,
    IReadOnlyDictionary<int, int> PollVoteCounts,
    int? MyPollVoteOption);