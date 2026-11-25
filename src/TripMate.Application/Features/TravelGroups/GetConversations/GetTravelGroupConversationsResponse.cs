namespace TripMate.Application.Features.TravelGroups.GetConversations;

public sealed record GetTravelGroupConversationsResponse(
    IReadOnlyList<TravelGroupConversationResponse> Items,
    long? NextBeforeGroupId);

public sealed record TravelGroupConversationResponse(
    long GroupId,
    string GroupName,
    long ItineraryId,
    string? ItineraryTitle,
    string ItinerarySourceType,
    long HostUserId,
    int MemberCount,
    string Status,
    string? LastMessage,
    DateTimeOffset? LastMessageAtUtc,
    int UnreadCount);