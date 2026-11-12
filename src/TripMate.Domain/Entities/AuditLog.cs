using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps dbo.AuditLogs in database/tripmate_schema_v7.sql.
/// </summary>
public class AuditLog : BaseEntity
{
    public const int ReasonMaxLength = 1000;

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

    /// <summary>
    /// Correlates the durable admission and outcome events for one UC-43 verification attempt.
    /// Null is reserved for audit records that did not originate in the check-in journal.
    /// </summary>
    public Guid? ScanAttemptId { get; private set; }

    /// <summary>
    /// Idempotency identity of the immutable journal event projected into this row.
    /// </summary>
    public Guid? JournalEventId { get; private set; }

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

        if (reason?.Length > ReasonMaxLength)
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

    public static AuditLog CreateCheckInJournalProjection(
        long actorUserId,
        long scheduleId,
        long? ticketId,
        Guid scanAttemptId,
        Guid journalEventId,
        string reasonCode,
        DateTimeOffset occurredAtUtc)
    {
        if (scanAttemptId == Guid.Empty)
        {
            throw new ArgumentException("Scan attempt identity must not be empty.", nameof(scanAttemptId));
        }

        if (journalEventId == Guid.Empty)
        {
            throw new ArgumentException("Journal event identity must not be empty.", nameof(journalEventId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        if (scheduleId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scheduleId));
        }

        var audit = CreateRecordedOutcome(
            actorUserId,
            AuditActionTypes.CheckInScanRejected,
            AuditEntityTypes.CheckIn,
            ticketId,
            occurredAtUtc,
            AuditOutcome.Failure,
            reasonCode,
            afterData: System.Text.Json.JsonSerializer.Serialize(new { scheduleId }));
        audit.ScanAttemptId = scanAttemptId;
        audit.JournalEventId = journalEventId;
        return audit;
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

    public static AuditLog CreateOperatorApplicationRejected(
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
            actionType: AuditActionTypes.OperatorApplicationReject,
            affectedEntity: AuditEntityTypes.OperatorProfile,
            affectedEntityId: operatorUserId,
            createdAtUtc: createdAtUtc,
            beforeData: beforeData,
            afterData: afterData,
            result: AuditOutcome.Success);
    }

    public static AuditLog CreateOperatorApplicationResubmitted(
        long actorUserId,
        string beforeData,
        string afterData,
        DateTimeOffset createdAtUtc)
    {
        if (actorUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actorUserId));
        }

        if (string.IsNullOrWhiteSpace(beforeData) || string.IsNullOrWhiteSpace(afterData))
        {
            throw new ArgumentException("Audit data is required.");
        }

        return CreateRecordedOutcome(
            actorUserId,
            AuditActionTypes.OperatorApplicationResubmit,
            AuditEntityTypes.OperatorProfile,
            actorUserId,
            createdAtUtc,
            AuditOutcome.Success,
            beforeData: beforeData,
            afterData: afterData);
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
            Result = AuditOutcome.Success,
            IpAddress = ipAddress,
            CreatedAtUtc = occurredAtUtc,
        };
    }

    // ========== UC-45 Export Revenue Report Factory Method ==========

    /// <summary>
    /// Records only the filter snapshot, format, scope, and generation mode (spec §Audit) —
    /// never generated bytes, payment identifiers, or a signed download URL.
    /// </summary>
    public static AuditLog CreateRevenueExportRequested(
        long actorUserId,
        long exportId,
        string afterData,
        DateTimeOffset createdAtUtc)
    {
        if (actorUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actorUserId));
        }

        if (exportId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exportId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(afterData);

        return CreateRecordedOutcome(
            actorUserId: actorUserId,
            actionType: AuditActionTypes.RevenueExportRequested,
            affectedEntity: AuditEntityTypes.RevenueExport,
            affectedEntityId: exportId,
            createdAtUtc: createdAtUtc,
            afterData: afterData,
            result: AuditOutcome.Success);
    }

    /// <summary>
    /// UC-63 BR-130: one row per successful export response (fresh or idempotent replay).
    /// Records only the report type, period, and filters — never the generated figures or file bytes.
    /// </summary>
    public static AuditLog CreateStatisticalReportExportRequested(
        long actorUserId,
        long exportOperationId,
        string afterData,
        DateTimeOffset createdAtUtc)
    {
        if (actorUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actorUserId));
        }

        if (exportOperationId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exportOperationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(afterData);

        return CreateRecordedOutcome(
            actorUserId: actorUserId,
            actionType: AuditActionTypes.StatisticalReportExportRequested,
            affectedEntity: AuditEntityTypes.StatisticalReportExport,
            affectedEntityId: exportOperationId,
            createdAtUtc: createdAtUtc,
            afterData: afterData,
            result: AuditOutcome.Success);
    }

    // ========== UC-07 Change Password Factory Method ==========

    /// <summary>
    /// Subject/action/timestamp only (spec §3.3/§6) — no password, hash, or token value is ever
    /// placed in BeforeData/AfterData for this action type.
    /// </summary>
    public static AuditLog CreateChangePassword(
        long actorUserId,
        DateTimeOffset occurredAtUtc,
        string? ipAddress = null)
    {
        if (actorUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actorUserId));
        }

        return new AuditLog
        {
            ActorUserId = actorUserId,
            ActionType = AuditActionTypes.AuthChangePassword,
            AffectedEntity = AuditEntityTypes.User,
            AffectedEntityId = actorUserId,
            Result = AuditOutcome.Success,
            IpAddress = ipAddress,
            CreatedAtUtc = occurredAtUtc,
        };
    }
}