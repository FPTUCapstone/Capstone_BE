using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>Append-only Staff decisions and party evidence submissions; never changes an existing refund.</summary>
public sealed class ComplaintStaffReviewEvent : BaseEntity
{
    public const string RequestEvidence = "RequestEvidence";
    public const string Reject = "Reject";
    public const string RefundFull = "RefundFull";
    public const string RefundPartial = "RefundPartial";
    public const string EvidenceSubmitted = "EvidenceSubmitted";
    public const string EvidenceExpired = "EvidenceExpired";
    public const string ReconsiderationRequested = "ReconsiderationRequested";
    public const string Traveler = "Traveler";
    public const string Operator = "Operator";
    public const string AuditEntity = "PostTripComplaint";
    public const int TextMaxLength = 1000;
    private ComplaintStaffReviewEvent() { }
    public long ComplaintId { get; private set; }
    public long? ActorUserId { get; private set; }
    public Guid OperationId { get; private set; }
    public string PayloadFingerprint { get; private set; } = "";
    public string Action { get; private set; } = "";
    public string Reason { get; private set; } = "";
    public string ContractBasis { get; private set; } = "";
    public decimal? Amount { get; private set; }
    public string? TargetParty { get; private set; }
    public long? RequestEventId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? DueAtUtc { get; private set; }
    public static ComplaintStaffReviewEvent Create(long complaintId, long? actorUserId, Guid operationId,
        string fingerprint, string action, string reason, string contractBasis, decimal? amount,
        string? targetParty, DateTimeOffset now, long? requestEventId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(complaintId);
        if (actorUserId is <= 0) throw new ArgumentOutOfRangeException(nameof(actorUserId));
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (operationId == Guid.Empty || fingerprint.Length != 64 || reason.Trim().Length > TextMaxLength ||
            contractBasis.Trim().Length > TextMaxLength) throw new ArgumentException("Invalid review metadata.");
        return new ComplaintStaffReviewEvent
        {
            ComplaintId = complaintId,
            ActorUserId = actorUserId,
            OperationId = operationId,
            PayloadFingerprint = fingerprint,
            Action = action,
            Reason = reason.Trim(),
            ContractBasis = contractBasis.Trim(),
            Amount = amount,
            TargetParty = targetParty,
            CreatedAtUtc = now.ToUniversalTime(),
            RequestEventId = requestEventId,
            DueAtUtc = action == RequestEvidence ? now.ToUniversalTime().AddHours(72) : null
        };
    }
}