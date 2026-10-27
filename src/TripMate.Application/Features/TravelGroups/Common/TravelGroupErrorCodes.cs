namespace TripMate.Application.Features.TravelGroups.Common;

/**
 * [UC-17] Travel Group Error Codes
 * Error codes returned when travel group domain/business operations fail.
 */
public static class TravelGroupErrorCodes
{
    public const string ItineraryNotFound = "travel_group.itinerary_not_found";
    public const string IdempotencyKeyPayloadMismatch = "travel_group.idempotency_key_payload_mismatch";
    public const string GroupNotFound = "travel_group.group_not_found";
    public const string HostPermissionRequired = "travel_group.host_permission_required";
    public const string InvitationCodeGenerationFailed = "travel_group.invitation_code_generation_failed";
    public const string InvitationUnavailable = "travel_group.invitation_unavailable";
    public const string AlreadyActiveMember = "travel_group.already_active_member";
    public const string ActiveMembershipRequired = "travel_group.active_membership_required";
    public const string MemberListInconsistent = "travel_group.member_list_inconsistent";
    public const string GroupClosed = "travel_group.group_closed";
    public const string AlreadyLeft = "travel_group.already_left";
    public const string MemberNotActive = "travel_group.member_not_active";
    public const string ConcurrentRetryExhausted = "travel_group.concurrent_retry_exhausted";
}