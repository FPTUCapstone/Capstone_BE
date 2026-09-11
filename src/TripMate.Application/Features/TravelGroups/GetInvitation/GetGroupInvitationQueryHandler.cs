using System.Security.Cryptography;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Constants;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.TravelGroups.GetInvitation;

/**
 * [UC-18] Get Group Invitation Query Handler
 * Retrieves an active invitation code and QR deep link for a travel group.
 * Generates a new invitation if none exists or if the existing one is expired.
 *
 * Requirements:
 *   - AC-01: Caller must be the Group Host (HostUserId == CurrentUserId).
 *   - AC-02: Target group must exist.
 *   - AC-03: Returns active unexpired invitation if exists; generates new 8-char code otherwise.
 *   - AC-04: Group members table is never modified.
 */
public class GetGroupInvitationQueryHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<GetGroupInvitationQuery, Result<GetGroupInvitationResponse>>
{
    public async Task<Result<GetGroupInvitationResponse>> Handle(
        GetGroupInvitationQuery request,
        CancellationToken cancellationToken)
    {
        // Step 1: Validate group existence
        var group = await dbContext.TravelGroups
            .FirstOrDefaultAsync(g => g.Id == request.GroupId, cancellationToken);

        if (group is null)
        {
            return Result.Failure<GetGroupInvitationResponse>(
                TravelGroupErrorCodes.GroupNotFound,
                "The specified travel group does not exist.");
        }

        // Step 2: Validate caller is the Group Host (AC-01)
        if (group.HostUserId != request.CurrentUserId)
        {
            return Result.Failure<GetGroupInvitationResponse>(
                TravelGroupErrorCodes.HostPermissionRequired,
                "You do not have permission to access this function.");
        }

        var now = dateTimeProvider.UtcNow;

        // Step 3: Check for existing active, unexpired invitation (AC-03)
        var activeInvitation = await dbContext.GroupInvitations
            .Where(i => i.GroupId == group.Id && i.ExpiresAtUtc > now && i.UsedCount < i.MaxUses)
            .OrderByDescending(i => i.ExpiresAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (activeInvitation is null)
        {
            // Generate and persist new invitation
            var inviteCode = GenerateInviteCode();
            var expiresAt = now.AddDays(TravelGroupConstants.InviteCodeExpirationDays);

            activeInvitation = new GroupInvitation
            {
                GroupId = group.Id,
                InviteCode = inviteCode,
                CreatedBy = request.CurrentUserId,
                ExpiresAtUtc = expiresAt,
                MaxUses = TravelGroupConstants.DefaultMaxUses,
                UsedCount = 0,
                CreatedAtUtc = now
            };

            dbContext.GroupInvitations.Add(activeInvitation);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var qrData = $"tripmate://groups/join?code={activeInvitation.InviteCode}";

        return Result.Success(new GetGroupInvitationResponse(
            group.Id,
            group.Name ?? string.Empty,
            activeInvitation.InviteCode,
            qrData,
            activeInvitation.ExpiresAtUtc));
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