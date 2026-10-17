namespace TripMate.Domain.Entities;

/// <summary>Durable idempotency record for a UC-27 booking-create request.</summary>
public sealed class TourBookingCreateOperation
{
    public const int PayloadFingerprintLength = 64;

    private TourBookingCreateOperation()
    {
    }

    public long Id { get; private set; }

    public long TravelerUserId { get; private set; }

    public Guid IdempotencyKey { get; private set; }

    public string PayloadFingerprint { get; private set; } = string.Empty;

    public long? BookingId { get; private set; }

    public Booking? Booking { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public static TourBookingCreateOperation Create(
        long travelerUserId,
        Guid idempotencyKey,
        string payloadFingerprint,
        Booking booking,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(booking);
        if (travelerUserId <= 0) throw new ArgumentOutOfRangeException(nameof(travelerUserId));
        if (idempotencyKey == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(idempotencyKey));
        if (string.IsNullOrWhiteSpace(payloadFingerprint) ||
            payloadFingerprint.Length != PayloadFingerprintLength)
            throw new ArgumentException("A SHA-256 payload fingerprint is required.", nameof(payloadFingerprint));

        return new TourBookingCreateOperation
        {
            TravelerUserId = travelerUserId,
            IdempotencyKey = idempotencyKey,
            PayloadFingerprint = payloadFingerprint,
            Booking = booking,
            CreatedAtUtc = nowUtc.ToUniversalTime(),
            CompletedAtUtc = nowUtc.ToUniversalTime(),
        };
    }
}
