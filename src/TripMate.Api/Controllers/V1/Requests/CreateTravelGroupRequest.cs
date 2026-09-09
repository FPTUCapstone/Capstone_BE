namespace TripMate.Api.Controllers.V1.Requests;

/**
 * [UC-17] Create Travel Group Request Body
 *
 * Properties:
 *   - ItineraryId (long): Target itinerary ID (required).
 *   - GroupName (string): Group name, max 150 characters (required).
 *   - HostUserId (long?): Optional host user ID fallback for internal testing.
 */
public record CreateTravelGroupRequest(
    long ItineraryId,
    string GroupName,
    long? HostUserId = null);