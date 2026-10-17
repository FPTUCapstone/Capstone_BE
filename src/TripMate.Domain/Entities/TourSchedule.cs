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

    public int MinimumCapacity { get; private set; }

    public DateTimeOffset? RegistrationDeadlineAtUtc { get; private set; }

    public decimal? AdultPrice { get; private set; }

    public decimal? ChildPrice { get; private set; }

    public decimal? InfantPrice { get; private set; }

    public int ReservedCapacity { get; private set; }

    public TourScheduleStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static TourSchedule Create(DateTimeOffset startAtUtc, DateTimeOffset endAtUtc,
        string? meetingPoint, int totalCapacity, DateTimeOffset createdAtUtc,
        int minimumCapacity = 1, DateTimeOffset? registrationDeadlineAtUtc = null,
        decimal? adultPrice = null, decimal? childPrice = null, decimal? infantPrice = null)
    {
        if (endAtUtc <= startAtUtc) throw new ArgumentOutOfRangeException(nameof(endAtUtc));
        if (totalCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(totalCapacity));
        if (minimumCapacity <= 0 || minimumCapacity > totalCapacity)
            throw new ArgumentOutOfRangeException(nameof(minimumCapacity));
        if (registrationDeadlineAtUtc.HasValue && registrationDeadlineAtUtc >= startAtUtc)
            throw new ArgumentOutOfRangeException(nameof(registrationDeadlineAtUtc));
        if (adultPrice is <= 0 || childPrice is < 0 || infantPrice is < 0)
            throw new ArgumentOutOfRangeException(nameof(adultPrice));
        if (HasFraction(adultPrice) || HasFraction(childPrice) || HasFraction(infantPrice))
            throw new ArgumentOutOfRangeException(nameof(adultPrice), "Prices must be whole VND amounts.");
        var normalizedMeetingPoint = string.IsNullOrWhiteSpace(meetingPoint) ? null : meetingPoint.Trim();
        if (normalizedMeetingPoint?.Length > MeetingPointMaxLength)
            throw new ArgumentOutOfRangeException(nameof(meetingPoint));
        return new TourSchedule
        {
            StartAtUtc = startAtUtc.ToUniversalTime(),
            EndAtUtc = endAtUtc.ToUniversalTime(),
            MeetingPoint = normalizedMeetingPoint,
            TotalCapacity = totalCapacity,
            MinimumCapacity = minimumCapacity,
            RegistrationDeadlineAtUtc = registrationDeadlineAtUtc?.ToUniversalTime(),
            AdultPrice = adultPrice,
            ChildPrice = childPrice,
            InfantPrice = infantPrice,
            ReservedCapacity = 0,
            Status = TourScheduleStatus.Scheduled,
            CreatedAtUtc = createdAtUtc.ToUniversalTime(),
        };
    }

    internal void AttachTo(Tour tour)
    {
        Tour = tour ?? throw new ArgumentNullException(nameof(tour));
    }

    private static bool HasFraction(decimal? price) => price.HasValue && price.Value != decimal.Truncate(price.Value);
}