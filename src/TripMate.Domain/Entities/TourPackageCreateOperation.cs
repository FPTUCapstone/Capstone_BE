namespace TripMate.Domain.Entities;

/// <summary>Durable idempotency record for a package-create request.</summary>
public sealed class TourPackageCreateOperation
{
    public const int PayloadFingerprintLength = 64;

    private TourPackageCreateOperation()
    {
    }

    public long Id { get; private set; }

    public long ActorUserId { get; private set; }

    public Guid IdempotencyKey { get; private set; }

    public string PayloadFingerprint { get; private set; } = string.Empty;

    public long? TourId { get; private set; }

    public Tour? Tour { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public static TourPackageCreateOperation Create(
        long actorUserId,
        Guid idempotencyKey,
        string payloadFingerprint,
        Tour tour,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(tour);
        if (actorUserId <= 0) throw new ArgumentOutOfRangeException(nameof(actorUserId));
        if (idempotencyKey == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(idempotencyKey));
        if (string.IsNullOrWhiteSpace(payloadFingerprint) ||
            payloadFingerprint.Length != PayloadFingerprintLength)
            throw new ArgumentException("A SHA-256 payload fingerprint is required.", nameof(payloadFingerprint));

        return new TourPackageCreateOperation
        {
            ActorUserId = actorUserId,
            IdempotencyKey = idempotencyKey,
            PayloadFingerprint = payloadFingerprint,
            Tour = tour,
            CreatedAtUtc = nowUtc.ToUniversalTime(),
            CompletedAtUtc = nowUtc.ToUniversalTime(),
        };
    }
}