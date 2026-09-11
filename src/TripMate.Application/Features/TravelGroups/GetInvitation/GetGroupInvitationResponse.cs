namespace TripMate.Application.Features.TravelGroups.GetInvitation;

/**
 * [UC-18] Get Group Invitation Response
 * Exposes invitation code, deep-link QR data, and expiry for group sharing.
 */
public sealed record GetGroupInvitationResponse(
    long GroupId,
    string GroupName,
    string InviteCode,
    string QrData,
    DateTimeOffset ExpiresAt);

