namespace TripMate.Application.Features.TravelGroups.CreateTravelGroup;

/**
 * [UC-17] Create Travel Group Response DTO
 *
 * Properties:
 *   - GroupId (long): Generated primary key for the new travel group.
 *   - GroupName (string): Validated name of the group.
 *   - ItineraryId (long): Linked itinerary ID.
 *   - HostUserId (long): ID of the creator assigned as Host.
 *   - CreatedAtUtc (DateTimeOffset): UTC timestamp of creation.
 */
public record CreateTravelGroupResponse(
    long GroupId,
    string GroupName,
    long ItineraryId,
    long HostUserId,
    DateTimeOffset CreatedAtUtc);