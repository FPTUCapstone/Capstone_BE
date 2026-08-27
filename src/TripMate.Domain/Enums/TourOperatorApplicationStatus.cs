namespace TripMate.Domain.Enums;

/// <summary>
/// Sign-in must succeed for a Tour Operator whose application is PendingApproval or Rejected
/// (BR3) — this status only gates Tour Operator features, never authentication itself.
/// </summary>
public enum TourOperatorApplicationStatus
{
    NotApplicable = 0,
    PendingApproval = 1,
    Approved = 2,
    Rejected = 3,
}
