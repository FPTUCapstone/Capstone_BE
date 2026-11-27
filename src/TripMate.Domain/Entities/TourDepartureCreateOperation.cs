namespace TripMate.Domain.Entities;

/// <summary>Durable idempotency record for adding a new departure schedule to an approved/published tour.</summary>
public sealed class TourDepartureCreateOperation
{
    public const int PayloadFingerprintLength = 64;

    private TourDepartureCreateOperation()
    {
    }

    public long Id { get; private set; }

    public long ActorUserId { get; private set; }

    public long TourId { get; private set; }

    public Tour Tour { get; private set; } = null!;

    public Guid IdempotencyKey { get; private set; }

    public string PayloadFingerprint { get; private set; } = string.Empty;

    public long? ScheduleId { get; private set; }

    public TourSchedule? Schedule { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public static TourDepartureCreateOperation Create(
        long actorUserId,
        Tour tour,
        Guid idempotencyKey,
        string payloadFingerprint,
        TourSchedule schedule,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(tour);
        ArgumentNullException.ThrowIfNull(schedule);
        if (actorUserId <= 0) throw new ArgumentOutOfRangeException(nameof(actorUserId));
        if (idempotencyKey == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(idempotencyKey));
        if (string.IsNullOrWhiteSpace(payloadFingerprint) ||
            payloadFingerprint.Length != PayloadFingerprintLength)
            throw new ArgumentException("A SHA-256 payload fingerprint is required.", nameof(payloadFingerprint));

        DateTimeOffset normalizedNow = nowUtc.ToUniversalTime();
        return new TourDepartureCreateOperation
        {
            ActorUserId = actorUserId,
            Tour = tour,
            IdempotencyKey = idempotencyKey,
            PayloadFingerprint = payloadFingerprint,
            Schedule = schedule,
            CreatedAtUtc = normalizedNow,
            CompletedAtUtc = normalizedNow,
        };
    }
}