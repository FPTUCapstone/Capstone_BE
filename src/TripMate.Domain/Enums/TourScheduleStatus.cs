namespace TripMate.Domain.Enums;

/// <summary>
/// Matches the commerce.TourSchedules.status SQL check constraint.
/// </summary>
public enum TourScheduleStatus
{
    Scheduled = 1,
    Cancelled = 2,
    Completed = 3,
}