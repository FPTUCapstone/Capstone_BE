namespace TripMate.Domain.Enums;

/// <summary>
/// Matches the commerce.TourPendingChangeSets.status SQL check constraint. No Cancelled value —
/// discard (TM-82) deletes the row outright while Draft or Rejected instead of transitioning it.
/// </summary>
public enum TourPendingChangeStatus
{
    Draft = 1,
    PendingReview = 2,
    Applied = 3,
    Rejected = 4,
}