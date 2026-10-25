namespace TripMate.Application.Features.TravelGroups.LeaveTravelGroup;

public sealed record LeaveTravelGroupResponse(
    long GroupId,
    string MembershipStatus,
    bool GroupClosed,
    long? NewHostUserId,
    DateTimeOffset LeftAtUtc);