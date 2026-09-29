namespace TripMate.Domain.Enums;

/// <summary>
/// Matches the commerce.Tours.status SQL check constraint.
/// </summary>
public enum TourStatus
{
    Draft = 1,
    Pending = 2,
    Approved = 3,
    Rejected = 4,
    Inactive = 5,
}