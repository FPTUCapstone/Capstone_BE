namespace TripMate.Application.Features.TravelGroups.Common;

/**
 * [UC-17] Travel Group Error Codes
 * Error codes returned when travel group domain/business operations fail.
 */
public static class TravelGroupErrorCodes
{
    public const string ItineraryNotFound = "travel_group.itinerary_not_found";
    public const string GroupNotFound = "TravelGroup.GroupNotFound";
    public const string HostPermissionRequired = "TravelGroup.HostPermissionRequired";
}