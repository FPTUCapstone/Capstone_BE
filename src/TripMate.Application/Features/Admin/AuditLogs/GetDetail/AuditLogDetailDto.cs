using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.AuditLogs.GetDetail;

public record AuditLogDetailDto(
    long Id,
    string ActionType,
    long? ActorUserId,
    string? ActorEmail,
    string ActorFullName,
    UserRole? ActorRole,
    string AffectedEntity,
    long? AffectedEntityId,
    string? BeforeData,
    string? AfterData,
    string? IpAddress,
    DateTimeOffset CreatedAtUtc,
    string CreatedAtLocal
);
