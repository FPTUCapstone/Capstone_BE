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

    /// <summary>
    /// BR-63 effective-price policy (UC-27): Adult uses this schedule's override or the tour's
    /// BasePrice; Child/Infant use their own override or fall back to the resolved Adult price.
    /// Null on Child/Infant means "no override", never "not permitted" — an age or
    /// allowed-participant-type rule must be modelled separately, not inferred from this method.
    /// </summary>
    public decimal GetEffectivePrice(ParticipantType participantType, decimal tourBasePrice)
    {
        var effectiveAdultPrice = AdultPrice ?? tourBasePrice;
        return participantType switch
        {
            ParticipantType.Adult => effectiveAdultPrice,
            ParticipantType.Child => ChildPrice ?? effectiveAdultPrice,
            ParticipantType.Infant => InfantPrice ?? effectiveAdultPrice,
            _ => throw new ArgumentOutOfRangeException(nameof(participantType)),
        };
    }

    public int RemainingCapacity => TotalCapacity - ReservedCapacity;

    /// <summary>Soft-reserves seats for a PendingPayment booking (UC-27). Caller must hold a
    /// row lock/serializable read first; this only re-asserts the invariant, it does not lock.</summary>
    public void ReserveCapacity(int participantCount)
    {
        if (participantCount <= 0) throw new ArgumentOutOfRangeException(nameof(participantCount));
        if (participantCount > RemainingCapacity)
            throw new InvalidOperationException("Not enough remaining capacity for this schedule.");
        ReservedCapacity += participantCount;
    }

    public bool IsOpenForBooking(DateTimeOffset nowUtc) =>
        Status == TourScheduleStatus.Scheduled &&
        (!RegistrationDeadlineAtUtc.HasValue || nowUtc < RegistrationDeadlineAtUtc.Value) &&
        nowUtc < StartAtUtc;

    private static bool HasFraction(decimal? price) => price.HasValue && price.Value != decimal.Truncate(price.Value);
}