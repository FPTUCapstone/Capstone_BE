using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Records an owner mutation so a retried request can replay the same successor version.
/// </summary>
public sealed class ItineraryVersionOperation : BaseEntity
{
    private ItineraryVersionOperation()
    {
    }

    private ItineraryVersionOperation(
        long travelerUserId,
        long sourceItineraryId,
        ItineraryVersionOperationType operationType,
        Guid idempotencyKey,
        string requestHash,
        DateTimeOffset createdAtUtc)
    {
        if (travelerUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(travelerUserId));
        }

        if (sourceItineraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceItineraryId));
        }

        if (!Enum.IsDefined(operationType))
        {
            throw new ArgumentOutOfRangeException(nameof(operationType));
        }

        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
        }

        if (string.IsNullOrWhiteSpace(requestHash))
        {
            throw new ArgumentException("A request hash is required.", nameof(requestHash));
        }

        TravelerUserId = travelerUserId;
        SourceItineraryId = sourceItineraryId;
        OperationType = operationType;
        IdempotencyKey = idempotencyKey;
        RequestHash = requestHash.Trim();
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
    }

    public static ItineraryVersionOperation Create(
        long travelerUserId,
        long sourceItineraryId,
        ItineraryVersionOperationType operationType,
        Guid idempotencyKey,
        string requestHash,
        DateTimeOffset createdAtUtc) =>
        new(travelerUserId, sourceItineraryId, operationType, idempotencyKey, requestHash, createdAtUtc);

    public long TravelerUserId { get; private set; }

    public long SourceItineraryId { get; private set; }

    public ItineraryVersionOperationType OperationType { get; private set; }

    public Guid IdempotencyKey { get; private set; }

    public string RequestHash { get; private set; } = string.Empty;

    public long? ResultItineraryId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public void Complete(long resultItineraryId)
    {
        if (resultItineraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resultItineraryId));
        }

        if (ResultItineraryId.HasValue)
        {
            throw new InvalidOperationException("The itinerary version operation is already complete.");
        }

        ResultItineraryId = resultItineraryId;
    }
}