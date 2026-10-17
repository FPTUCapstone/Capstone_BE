using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Read/write model for commerce.Bookings. UC-27 owns quote and creation of a PendingPayment
/// booking; UC-28 confirms payment, UC-41/42 own cancellation/refund, UC-43 owns check-in.
/// The table has no per-participant-type price columns, so UnitPrice stores the schedule's
/// effective Adult price as a reference/display value only. SubtotalAmount is never persisted —
/// callers derive it as TotalAmount + DiscountAmount, since both are stored.
/// </summary>
public class Booking : BaseEntity
{
    public const int BookingCodeMaxLength = 30;
    public const int CancelReasonMaxLength = 300;

    private readonly List<BookingParticipant> _participants = [];

    private Booking()
    {
    }

    public string BookingCode { get; private set; } = string.Empty;

    public long TravelerUserId { get; private set; }

    public long TourScheduleId { get; private set; }

    public TourSchedule TourSchedule { get; private set; } = null!;

    public long? VoucherId { get; private set; }

    public int Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public decimal DiscountAmount { get; private set; }

    public decimal TotalAmount { get; private set; }

    public BookingStatus Status { get; private set; }

    public PaymentStatus PaymentStatus { get; private set; }

    public DateTimeOffset BookedAtUtc { get; private set; }

    public DateTimeOffset? PaymentExpiresAtUtc { get; private set; }

    public DateTimeOffset? CancelledAtUtc { get; private set; }

    public string? CancelReason { get; private set; }

    public IReadOnlyCollection<BookingParticipant> Participants => _participants.AsReadOnly();

    /// <summary>Subtotal before voucher discount, derived from the two persisted amounts.</summary>
    public decimal SubtotalAmount => TotalAmount + DiscountAmount;

    public static Booking Create(string bookingCode, long travelerUserId, long tourScheduleId,
        long? voucherId, decimal unitPrice, decimal discountAmount, decimal totalAmount,
        IReadOnlyCollection<BookingParticipant> participants, DateTimeOffset bookedAtUtc,
        DateTimeOffset paymentExpiresAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bookingCode);
        var normalizedCode = bookingCode.Trim();
        if (normalizedCode.Length > BookingCodeMaxLength)
            throw new ArgumentOutOfRangeException(nameof(bookingCode));
        if (travelerUserId <= 0) throw new ArgumentOutOfRangeException(nameof(travelerUserId));
        if (tourScheduleId <= 0) throw new ArgumentOutOfRangeException(nameof(tourScheduleId));
        if (unitPrice < 0 || discountAmount < 0 || totalAmount < 0)
            throw new ArgumentOutOfRangeException(nameof(totalAmount));
        ArgumentNullException.ThrowIfNull(participants);
        if (participants.Count == 0)
            throw new ArgumentException("At least one participant is required.", nameof(participants));
        if (paymentExpiresAtUtc <= bookedAtUtc)
            throw new ArgumentOutOfRangeException(nameof(paymentExpiresAtUtc));

        var booking = new Booking
        {
            BookingCode = normalizedCode,
            TravelerUserId = travelerUserId,
            TourScheduleId = tourScheduleId,
            VoucherId = voucherId,
            Quantity = participants.Count,
            UnitPrice = unitPrice,
            DiscountAmount = discountAmount,
            TotalAmount = totalAmount,
            Status = BookingStatus.PendingPayment,
            PaymentStatus = PaymentStatus.Unpaid,
            BookedAtUtc = bookedAtUtc.ToUniversalTime(),
            PaymentExpiresAtUtc = paymentExpiresAtUtc.ToUniversalTime(),
        };
        foreach (var participant in participants)
        {
            participant.AttachTo(booking);
            booking._participants.Add(participant);
        }

        return booking;
    }

    /// <summary>BK-yyyyMMdd-NNNNN, e.g. BK-20261017-00001. Sequence is per-day, assigned by the caller.</summary>
    public static string GenerateCode(DateTimeOffset nowUtc, int dailySequence)
    {
        if (dailySequence is <= 0 or > 99_999)
            throw new ArgumentOutOfRangeException(nameof(dailySequence));
        return $"BK-{nowUtc.ToUniversalTime():yyyyMMdd}-{dailySequence:D5}";
    }
}
