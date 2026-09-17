using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps dbo.AuditLogs in database/tripmate_schema_v7.sql.
/// </summary>
public class AuditLog : BaseEntity
{
    public AuditLog()
    {
    }

    public AuditLog(
        long? actorUserId,
        string actionType,
        string affectedEntity,
        long? affectedEntityId,
        DateTimeOffset createdAtUtc,
        User? actorUser = null,
        string? beforeData = null,
        string? afterData = null,
        string? ipAddress = null)
    {
        ActorUserId = actorUserId;
        ActionType = actionType;
        AffectedEntity = affectedEntity;
        AffectedEntityId = affectedEntityId;
        CreatedAtUtc = createdAtUtc;
        ActorUser = actorUser;
        BeforeData = beforeData;
        AfterData = afterData;
        IpAddress = ipAddress;
    }

    public long? ActorUserId { get; private set; }

    public User? ActorUser { get; private set; }

    public string ActionType { get; private set; } = string.Empty;

    public string AffectedEntity { get; private set; } = string.Empty;

    public long? AffectedEntityId { get; private set; }

    public string? BeforeData { get; private set; }

    public string? AfterData { get; private set; }

    public string? IpAddress { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static AuditLog CreatePoiCreated(
        long actorUserId,
        long pointOfInterestId,
        string afterData,
        DateTimeOffset createdAtUtc)
    {
        if (actorUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actorUserId));
        }

        if (pointOfInterestId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pointOfInterestId));
        }

        if (string.IsNullOrWhiteSpace(afterData))
        {
            throw new ArgumentException("Audit data is required.", nameof(afterData));
        }

        return new AuditLog(
            actorUserId: actorUserId,
            actionType: AuditActionTypes.PoiCreate,
            affectedEntity: AuditEntityTypes.PointOfInterest,
            affectedEntityId: pointOfInterestId,
            createdAtUtc: createdAtUtc,
            afterData: afterData);
    }

    public static AuditLog CreateOperatorApplicationApproved(
        long actorUserId,
        long operatorUserId,
        string beforeData,
        string afterData,
        DateTimeOffset createdAtUtc)
    {
        if (actorUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actorUserId));
        }

        if (operatorUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(operatorUserId));
        }

        if (string.IsNullOrWhiteSpace(beforeData))
        {
            throw new ArgumentException("Audit data is required.", nameof(beforeData));
        }

        if (string.IsNullOrWhiteSpace(afterData))
        {
            throw new ArgumentException("Audit data is required.", nameof(afterData));
        }

        return new AuditLog(
            actorUserId: actorUserId,
            actionType: AuditActionTypes.OperatorApplicationApprove,
            affectedEntity: AuditEntityTypes.OperatorProfile,
            affectedEntityId: operatorUserId,
            createdAtUtc: createdAtUtc,
            beforeData: beforeData,
            afterData: afterData);
    }
}