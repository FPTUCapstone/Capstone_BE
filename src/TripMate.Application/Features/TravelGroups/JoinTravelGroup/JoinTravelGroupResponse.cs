namespace TripMate.Application.Features.TravelGroups.JoinTravelGroup;

public sealed record JoinTravelGroupResponse(
    long GroupId,
    string GroupName,
    long ItineraryId);