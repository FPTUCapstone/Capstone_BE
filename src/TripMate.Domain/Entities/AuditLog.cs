using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps dbo.AuditLogs in database/tripmate_schema_v7.sql.
/// </summary>
public class AuditLog : BaseEntity
{
    public long? ActorUserId { get; set; }

    public User? ActorUser { get; set; }

    public string ActionType { get; set; } = string.Empty;

    public string AffectedEntity { get; set; } = string.Empty;

    public long? AffectedEntityId { get; set; }

    public string? BeforeData { get; set; }

    public string? AfterData { get; set; }

    public string? IpAddress { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
