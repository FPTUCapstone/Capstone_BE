namespace TripMate.Application.Features.TravelGroups.GetMembers;

public sealed record GetTravelGroupMembersResponse(
    long GroupId,
    string GroupName,
    long ItineraryId,
    int MemberCount,
    IReadOnlyList<TravelGroupMemberResponse> Members);

public sealed record TravelGroupMemberResponse(
    long MemberId,
    string DisplayName,
    string? AvatarUrl,
    bool IsHost,
    DateTimeOffset JoinedAtUtc,
    bool LocationSharingEnabled);