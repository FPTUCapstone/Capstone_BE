using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.CreateTravelGroup;

/**
 * [UC-17] Create Travel Group Command Handler
 * Creates a travel group linked to an itinerary and assigns the creator as Host member.
 *
 * Input:
 *   - ItineraryId (long): Target itinerary ID (must exist in planning.Itineraries and be accessible).
 *   - GroupName (string): Group name, max 150 characters (required).
 *   - HostUserId (long): ID of the authenticated traveler creating the group.
 *
 * Output:
 *   - Success: Result<CreateTravelGroupResponse> with the created group details.
 *   - Failure: Error 404 (ItineraryNotFound) if itinerary does not exist or is inaccessible.
 */
public class CreateTravelGroupCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<CreateTravelGroupCommand, Result<CreateTravelGroupResponse>>
{
    public async Task<Result<CreateTravelGroupResponse>> Handle(
        CreateTravelGroupCommand request,
        CancellationToken cancellationToken)
    {
        // Step 1: Validate that the selected itinerary exists and is accessible (BR-41).
        // Access is determined by the itinerary access contract; UC-17 must not
        // impose an owner-only restriction that is not defined by the SRS.
        var itinerary = await dbContext.Itineraries.FirstOrDefaultAsync(
            i => i.Id == request.ItineraryId,
            cancellationToken);

        if (itinerary is null)
        {
            return Result.Failure<CreateTravelGroupResponse>(
                TravelGroupErrorCodes.ItineraryNotFound,
                "The specified itinerary does not exist or is inaccessible.");
        }

        // Step 2: Initialize travel group entity
        var travelGroup = new TravelGroup
        {
            ItineraryId = itinerary.Id,
            HostUserId = request.HostUserId,
            Name = request.GroupName.Trim(),
            CreatedAtUtc = dateTimeProvider.UtcNow
        };

        // Step 3: Assign creator as exclusive initial Group Host (BR-42)
        // Note: Assign navigation property (TravelGroup) instead of scalar GroupId
        // so EF Core automatically populates the generated key during SaveChangesAsync.
        var hostMember = new GroupMember
        {
            TravelGroup = travelGroup,
            UserId = request.HostUserId,
            LocationSharingEnabled = false,
            Status = GroupMemberStatus.Active,
            JoinedAtUtc = dateTimeProvider.UtcNow
        };

        // Step 4: Persist group and initial Host membership atomically.
        // Invitation generation belongs exclusively to UC-18.
        dbContext.TravelGroups.Add(travelGroup);
        dbContext.GroupMembers.Add(hostMember);

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new CreateTravelGroupResponse(
            travelGroup.Id,
            travelGroup.Name ?? string.Empty,
            travelGroup.ItineraryId,
            travelGroup.HostUserId,
            travelGroup.CreatedAtUtc));
    }
}
