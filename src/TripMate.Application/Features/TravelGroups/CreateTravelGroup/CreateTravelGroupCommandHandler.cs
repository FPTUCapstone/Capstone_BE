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
 *   - ItineraryId (long): Target itinerary ID (must exist in planning.Itineraries and belong to creator).
 *   - GroupName (string): Group name, max 150 characters (required).
 *   - HostUserId (long): ID of the authenticated traveler creating the group.
 *
 * Output:
 *   - Success: Result<CreateTravelGroupResponse> with the created group details.
 *   - Failure: Error 404 (ItineraryNotFound) if itinerary does not exist or does not belong to user.
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
        return await dbContext.ExecuteInTransactionAsync(async transactionCancellationToken =>
        {
            var previousRequest = await dbContext.TravelGroupCreationRequests
                .Include(operation => operation.TravelGroup)
                .FirstOrDefaultAsync(
                    operation => operation.TravelerUserId == request.HostUserId
                        && operation.IdempotencyKey == request.IdempotencyKey,
                    transactionCancellationToken);

            if (previousRequest is not null)
            {
                return Result.Success(ToResponse(previousRequest.TravelGroup));
            }

            // Step 1: Validate itinerary existence and ownership (BR-41).
            var itinerary = await dbContext.Itineraries.FirstOrDefaultAsync(
                itinerary => itinerary.Id == request.ItineraryId,
                transactionCancellationToken);

            if (itinerary is null || itinerary.TravelerUserId != request.HostUserId)
            {
                return Result.Failure<CreateTravelGroupResponse>(
                    TravelGroupErrorCodes.ItineraryNotFound,
                    "The specified itinerary does not exist or is inaccessible.");
            }

            var now = dateTimeProvider.UtcNow;
            var travelGroup = new TravelGroup
            {
                ItineraryId = itinerary.Id,
                HostUserId = request.HostUserId,
                Name = request.GroupName.Trim(),
                CreatedAtUtc = now
            };
            var hostMember = new GroupMember
            {
                TravelGroup = travelGroup,
                UserId = request.HostUserId,
                LocationSharingEnabled = false,
                Status = GroupMemberStatus.Active,
                JoinedAtUtc = now
            };
            var operation = new TravelGroupCreationRequest
            {
                TravelerUserId = request.HostUserId,
                IdempotencyKey = request.IdempotencyKey,
                TravelGroup = travelGroup,
                CreatedAtUtc = now
            };
            dbContext.TravelGroups.Add(travelGroup);
            dbContext.GroupMembers.Add(hostMember);
            dbContext.TravelGroupCreationRequests.Add(operation);
            await dbContext.SaveChangesAsync(transactionCancellationToken);

            return Result.Success(ToResponse(travelGroup));
        }, cancellationToken);
    }

    private static CreateTravelGroupResponse ToResponse(TravelGroup travelGroup) =>
        new(
            travelGroup.Id,
            travelGroup.Name ?? string.Empty,
            travelGroup.ItineraryId,
            travelGroup.HostUserId,
            travelGroup.CreatedAtUtc);
}
