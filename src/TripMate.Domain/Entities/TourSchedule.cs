using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Read model for a dated tour departure stored in commerce.TourSchedules.
/// </summary>
public class TourSchedule : BaseEntity
{
    public const int MeetingPointMaxLength = 500;

    private TourSchedule()
    {
    }

    public long TourId { get; private set; }

    public Tour Tour { get; private set; } = null!;

    public DateTimeOffset StartAtUtc { get; private set; }

    public DateTimeOffset EndAtUtc { get; private set; }

    public string? MeetingPoint { get; private set; }

    public int TotalCapacity { get; private set; }

    public int ReservedCapacity { get; private set; }

    public TourScheduleStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
}