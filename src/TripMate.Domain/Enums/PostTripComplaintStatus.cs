namespace TripMate.Domain.Enums;

/// <summary>
/// Status of a post-trip complaint, as listed in SRS §3.8.6.2 PC-01 and §3.8.6.3 PC-01.
/// Shared by UC-68, UC-69 and UC-75; TM-222 persists it.
/// </summary>
public enum PostTripComplaintStatus
{
    AwaitingOperatorResponse,
    AwaitingTravelerResponse,
    Approved,
    Rejected,
    Declined,
    Expired,
}
