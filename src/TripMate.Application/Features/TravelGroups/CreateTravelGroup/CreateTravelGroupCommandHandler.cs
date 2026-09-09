using System.Security.Cryptography;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Constants;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.CreateTravelGroup;

/**
 * [UC-17] Create Travel Group Command Handler
 * Creates a travel group linked to an itinerary, assigns creator as Host member,
 * and generates an active invitation code.
 *
 * Input:
 *   - ItineraryId (long): Target itinerary ID (must exist in planning.Itineraries and belong to creator).
 *   - GroupName (string): Group name, max 150 characters (required).
 *   - HostUserId (long): ID of the authenticated traveler creating the group.
 *
 * Output:
 *   - Success: Result<CreateTravelGroupResponse> with GroupId, GroupName, InviteCode.
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
        // Step 1: Validate itinerary existence and ownership (BR-41)
        var itinerary = await dbContext.Itineraries.FirstOrDefaultAsync(
            i => i.Id == request.ItineraryId,
            cancellationToken);

        if (itinerary is null || itinerary.TravelerUserId != request.HostUserId)
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

        // Step 4: Generate a unique invitation code
        var invitation = new GroupInvitation
        {
            TravelGroup = travelGroup,
            InviteCode = GenerateInviteCode(),
            CreatedBy = request.HostUserId,
            ExpiresAtUtc = dateTimeProvider.UtcNow.AddDays(TravelGroupConstants.InviteCodeExpirationDays),
            MaxUses = TravelGroupConstants.DefaultMaxUses,
            UsedCount = 0,
            CreatedAtUtc = dateTimeProvider.UtcNow
        };

        // Step 5: Persist graph atomically in a single transaction
        dbContext.TravelGroups.Add(travelGroup);
        dbContext.GroupMembers.Add(hostMember);
        dbContext.GroupInvitations.Add(invitation);

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new CreateTravelGroupResponse(
            travelGroup.Id,
            travelGroup.Name ?? string.Empty,
            travelGroup.ItineraryId,
            travelGroup.HostUserId,
            invitation.InviteCode,
            travelGroup.CreatedAtUtc));
    }

    private static string GenerateInviteCode()
    {
        var chars = new char[TravelGroupConstants.InviteCodeLength];
        var bytes = RandomNumberGenerator.GetBytes(chars.Length);

        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = TravelGroupConstants.InviteCodeCharacters[bytes[i] % TravelGroupConstants.InviteCodeCharacters.Length];
        }

        return new string(chars);
    }
}