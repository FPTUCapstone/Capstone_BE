using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public class AuditLog : BaseEntity
{
    private AuditLog()
    {
    }

    public long? ActorUserId { get; private set; }

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

        return new AuditLog
        {
            ActorUserId = actorUserId,
            ActionType = AuditActionTypes.PoiCreate,
            AffectedEntity = AuditEntityTypes.PointOfInterest,
            AffectedEntityId = pointOfInterestId,
            AfterData = afterData,
            CreatedAtUtc = createdAtUtc,
        };
    }
}