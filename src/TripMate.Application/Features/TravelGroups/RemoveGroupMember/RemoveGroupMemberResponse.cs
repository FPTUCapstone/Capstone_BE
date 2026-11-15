namespace TripMate.Application.Features.TravelGroups.RemoveGroupMember;

public sealed record RemoveGroupMemberResponse(
    long GroupId,
    long MemberUserId,
    string MemberName,
    DateTimeOffset RemovedAtUtc);