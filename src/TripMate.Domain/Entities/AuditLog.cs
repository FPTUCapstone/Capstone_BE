using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps dbo.AuditLogs in database/tripmate_schema_v7.sql.
/// </summary>
public class AuditLog : BaseEntity
{
    private AuditLog()
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
        ArgumentException.ThrowIfNullOrWhiteSpace(actionType);
        ArgumentException.ThrowIfNullOrWhiteSpace(affectedEntity);
        if (actionType.Length > 50 || affectedEntity.Length > 80 || ipAddress?.Length > 45)
        {
            throw new ArgumentException("Audit metadata exceeds its database column length.");
        }

        if (actorUserId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actorUserId));
        }

        if (affectedEntityId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(affectedEntityId));
        }

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

    // Null is reserved for historical records whose outcome was not captured.
    public AuditOutcome? Result { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static AuditLog CreateRecordedOutcome(
        long? actorUserId,
        string actionType,
        string affectedEntity,
        long? affectedEntityId,
        DateTimeOffset createdAtUtc,
        AuditOutcome result,
        string? reason = null,
        string? beforeData = null,
        string? afterData = null,
        string? ipAddress = null)
    {
        if (!Enum.IsDefined(result))
        {
            throw new ArgumentOutOfRangeException(nameof(result));
        }

        if (reason?.Length > 1000)
        {
            throw new ArgumentException("Audit reason cannot exceed 1000 characters.", nameof(reason));
        }

        return new AuditLog(actorUserId, actionType, affectedEntity, affectedEntityId,
            createdAtUtc, beforeData: beforeData, afterData: afterData, ipAddress: ipAddress)
        {
            Result = result,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()
        };
    }

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

        return CreateRecordedOutcome(
            actorUserId: actorUserId,
            actionType: AuditActionTypes.PoiCreate,
            affectedEntity: AuditEntityTypes.PointOfInterest,
            affectedEntityId: pointOfInterestId,
            createdAtUtc: createdAtUtc,
            afterData: afterData,
            result: AuditOutcome.Success);
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

        return CreateRecordedOutcome(
            actorUserId: actorUserId,
            actionType: AuditActionTypes.OperatorApplicationApprove,
            affectedEntity: AuditEntityTypes.OperatorProfile,
            affectedEntityId: operatorUserId,
            createdAtUtc: createdAtUtc,
            beforeData: beforeData,
            afterData: afterData,
            result: AuditOutcome.Success);
    }

    // ========== UC-05 Sign Out Factory Methods ==========

    public static AuditLog CreateSignOut(
        long actorUserId,
        long refreshTokenId,
        DateTimeOffset occurredAtUtc,
        string platform,
        string? traceId,
        string? ipAddress)
    {
        if (actorUserId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actorUserId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        return new AuditLog
        {
            ActorUserId = actorUserId,
            ActionType = AuditActionTypes.AuthSignOut,
            AffectedEntity = AuditEntityTypes.RefreshToken,
            AffectedEntityId = refreshTokenId,
            AfterData = System.Text.Json.JsonSerializer.Serialize(new
            {
                Result = "Success",
                Platform = platform,
                TraceId = traceId,
            }),
            IpAddress = ipAddress,
            CreatedAtUtc = occurredAtUtc,
        };
    }

    public static AuditLog CreateSignOutAll(
        long actorUserId,
        int revokedSessionCount,
        DateTimeOffset occurredAtUtc,
        string platform,
        string? traceId,
        string? ipAddress)
    {
        if (actorUserId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actorUserId));
        }

        if (revokedSessionCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revokedSessionCount));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        return new AuditLog
        {
            ActorUserId = actorUserId,
            ActionType = AuditActionTypes.AuthSignOutAll,
            AffectedEntity = AuditEntityTypes.RefreshToken,
            AffectedEntityId = null,
            AfterData = System.Text.Json.JsonSerializer.Serialize(new
            {
                Result = "Success",
                RevokedSessionCount = revokedSessionCount,
                Platform = platform,
                TraceId = traceId,
            }),
            IpAddress = ipAddress,
            CreatedAtUtc = occurredAtUtc,
        };
    }
}