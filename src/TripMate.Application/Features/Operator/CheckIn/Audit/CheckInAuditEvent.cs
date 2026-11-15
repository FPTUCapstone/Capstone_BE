using System.Security.Cryptography;
using System.Text;

namespace TripMate.Application.Features.Operator.CheckIn.Audit;

public enum CheckInAuditEventType
{
    Admission = 1,
    Rejected = 2,
}

public enum CheckInAuditWriteResult
{
    Confirmed = 1,
    Unavailable = 2,
    Uncertain = 3,
    Conflict = 4,
}

public sealed record CheckInAuditEvent
{
    private CheckInAuditEvent(
        Guid eventId,
        Guid scanAttemptId,
        CheckInAuditEventType eventType,
        DateTimeOffset occurredAtUtc,
        long actorUserId,
        long scheduleId,
        long? ticketId,
        string? reasonCode)
    {
        if (eventId == Guid.Empty) throw new ArgumentException("Event identity is required.", nameof(eventId));
        if (scanAttemptId == Guid.Empty) throw new ArgumentException("Attempt identity is required.", nameof(scanAttemptId));
        if (actorUserId <= 0) throw new ArgumentOutOfRangeException(nameof(actorUserId));
        if (scheduleId <= 0) throw new ArgumentOutOfRangeException(nameof(scheduleId));
        if (ticketId is <= 0) throw new ArgumentOutOfRangeException(nameof(ticketId));
        if (eventType == CheckInAuditEventType.Rejected && string.IsNullOrWhiteSpace(reasonCode))
            throw new ArgumentException("A rejection reason code is required.", nameof(reasonCode));
        if (eventType == CheckInAuditEventType.Admission && reasonCode is not null)
            throw new ArgumentException("Admission cannot carry a rejection reason.", nameof(reasonCode));

        EventId = eventId;
        ScanAttemptId = scanAttemptId;
        EventType = eventType;
        OccurredAtUtc = occurredAtUtc;
        ActorUserId = actorUserId;
        ScheduleId = scheduleId;
        TicketId = ticketId;
        ReasonCode = reasonCode;
    }

    public const int CurrentSchemaVersion = 1;

    public Guid EventId { get; }
    public Guid ScanAttemptId { get; }
    public CheckInAuditEventType EventType { get; }
    public DateTimeOffset OccurredAtUtc { get; }
    public long ActorUserId { get; }
    public long ScheduleId { get; }
    public long? TicketId { get; }
    public string? ReasonCode { get; }
    public int SchemaVersion => CurrentSchemaVersion;

    public static CheckInAuditEvent Admission(
        Guid eventId,
        Guid scanAttemptId,
        DateTimeOffset occurredAtUtc,
        long actorUserId,
        long scheduleId) =>
        new(eventId, scanAttemptId, CheckInAuditEventType.Admission, occurredAtUtc,
            actorUserId, scheduleId, null, null);

    public static CheckInAuditEvent Rejected(
        Guid eventId,
        Guid scanAttemptId,
        DateTimeOffset occurredAtUtc,
        long actorUserId,
        long scheduleId,
        long? ticketId,
        string reasonCode) =>
        new(eventId, scanAttemptId, CheckInAuditEventType.Rejected, occurredAtUtc,
            actorUserId, scheduleId, ticketId, reasonCode);

    public static Guid DeriveRejectionEventId(Guid scanAttemptId)
    {
        if (scanAttemptId == Guid.Empty)
        {
            throw new ArgumentException("Attempt identity is required.", nameof(scanAttemptId));
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"tripmate:checkin:audit:rejected:{scanAttemptId:N}"));
        return new Guid(digest.AsSpan(0, 16));
    }
}