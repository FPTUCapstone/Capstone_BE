using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TravelGroups.CreateTravelGroup;

/**
 * [UC-17] Create Travel Group Command
 *
 * Input:
 *   - ItineraryId (long): Target itinerary ID (required).
 *   - GroupName (string): User-defined group name (required, max 150 chars).
 *   - HostUserId (long): ID of the creator assigned as Group Host.
 *
 * Output:
 *   - Result<CreateTravelGroupResponse>: Details of newly created travel group and invite code.
 */
public record CreateTravelGroupCommand(
    long ItineraryId,
    string GroupName,
    long HostUserId) : IRequest<Result<CreateTravelGroupResponse>>;