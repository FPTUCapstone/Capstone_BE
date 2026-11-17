using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>Append-only commercial history for the bounded UC-75 negotiation cycle.</summary>
public sealed class PostTripComplaintNegotiationEvent : BaseEntity
{
    public const int ReasonMaxLength = 1000;

    private PostTripComplaintNegotiationEvent() { }

    public long ComplaintId { get; private set; }
    public int Round { get; private set; }
    public long ActorUserId { get; private set; }
    public PostTripComplaintNegotiationActor Actor { get; private set; }
    public PostTripComplaintNegotiationEventType EventType { get; private set; }
    public decimal? Amount { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static PostTripComplaintNegotiationEvent Create(long complaintId, int round, long actorUserId,
        PostTripComplaintNegotiationActor actor, PostTripComplaintNegotiationEventType eventType,
        decimal? amount, string? reason, DateTimeOffset createdAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(complaintId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(actorUserId);
        if (round is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(round));
        if (amount is <= 0m) throw new ArgumentOutOfRangeException(nameof(amount));
        string? normalizedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (normalizedReason?.Length > ReasonMaxLength) throw new ArgumentOutOfRangeException(nameof(reason));
        return new PostTripComplaintNegotiationEvent
        {
            ComplaintId = complaintId,
            Round = round,
            ActorUserId = actorUserId,
            Actor = actor,
            EventType = eventType,
            Amount = amount,
            Reason = normalizedReason,
            CreatedAtUtc = createdAtUtc.ToUniversalTime(),
        };
    }
}