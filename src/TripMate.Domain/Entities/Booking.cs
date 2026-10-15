using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public sealed class Booking : BaseEntity
{
    public const int CancelReasonMaxLength = 300;
    public const int PolicySnapshotMaxLength = 500;
    public const string DefaultCancellationPolicySnapshot =
        "{\"atLeastSevenDaysPercent\":100,\"threeToSevenDaysPercent\":70,\"oneToThreeDaysPercent\":30}";

    private readonly List<Ticket> _tickets = [];

    private Booking()
    {
    }

    public long TravelerUserId { get; private set; }
    public long? TourScheduleId { get; private set; }
    public TourSchedule? TourSchedule { get; private set; }
    public int Quantity { get; private set; }
    public decimal TotalAmount { get; private set; }
    public BookingStatus Status { get; private set; }
    public PaymentStatus PaymentStatus { get; private set; }
    public DateTimeOffset? CancelledAtUtc { get; private set; }
    public string? CancelReason { get; private set; }
    public string CancellationPolicySnapshot { get; private set; } = DefaultCancellationPolicySnapshot;
    public IReadOnlyCollection<Ticket> Tickets => _tickets.AsReadOnly();

    public static Booking Restore(long id, long travelerUserId, long tourScheduleId, int quantity,
        decimal totalAmount, BookingStatus status, PaymentStatus paymentStatus,
        string cancellationPolicySnapshot)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(travelerUserId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tourScheduleId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        ArgumentOutOfRangeException.ThrowIfNegative(totalAmount);
        ArgumentException.ThrowIfNullOrWhiteSpace(cancellationPolicySnapshot);

        return new Booking
        {
            Id = id,
            TravelerUserId = travelerUserId,
            TourScheduleId = tourScheduleId,
            Quantity = quantity,
            TotalAmount = totalAmount,
            Status = status,
            PaymentStatus = paymentStatus,
            CancellationPolicySnapshot = cancellationPolicySnapshot,
        };
    }

    /// <summary>
    /// Creates a new booking with an immutable cancellation-policy snapshot. The snapshot is
    /// written by the domain instead of relying on a database default so every persistence
    /// provider observes the same contract.
    /// </summary>
    public static Booking Create(long travelerUserId, long tourScheduleId, int quantity,
        decimal totalAmount, BookingStatus status, PaymentStatus paymentStatus,
        string? cancellationPolicySnapshot = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(travelerUserId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tourScheduleId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        ArgumentOutOfRangeException.ThrowIfNegative(totalAmount);

        string snapshot = cancellationPolicySnapshot ?? DefaultCancellationPolicySnapshot;
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot);
        if (snapshot.Length > PolicySnapshotMaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(cancellationPolicySnapshot));
        }

        return new Booking
        {
            TravelerUserId = travelerUserId,
            TourScheduleId = tourScheduleId,
            Quantity = quantity,
            TotalAmount = totalAmount,
            Status = status,
            PaymentStatus = paymentStatus,
            CancellationPolicySnapshot = snapshot,
        };
    }

    public void Cancel(DateTimeOffset cancelledAtUtc, string? reason)
    {
        if (Status is not (BookingStatus.PendingPayment or BookingStatus.Confirmed))
        {
            throw new InvalidOperationException("Only pending or confirmed bookings can be cancelled.");
        }

        if (reason?.Length > CancelReasonMaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        Status = BookingStatus.Cancelled;
        CancelledAtUtc = cancelledAtUtc;
        CancelReason = reason;
    }
}