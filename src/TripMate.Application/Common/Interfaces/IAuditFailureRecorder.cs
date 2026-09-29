namespace TripMate.Application.Common.Interfaces;

public sealed record AuditFailureEvent(long ActorUserId, string ActionType, string AffectedEntity,
    long? AffectedEntityId, string ErrorCode);

/// <summary>Persists safe failure metadata outside the failed business unit of work.</summary>
public interface IAuditFailureRecorder
{
    Task RecordAsync(AuditFailureEvent auditEvent);
}