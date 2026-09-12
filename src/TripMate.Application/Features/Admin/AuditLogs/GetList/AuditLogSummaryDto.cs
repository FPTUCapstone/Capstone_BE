using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.AuditLogs.GetList;

public record AuditLogSummaryDto(
    long Id,
    string ActionType,
    long? ActorUserId,
    string? ActorEmail,
    string? ActorFullName,
    UserRole? ActorRole,
    string AffectedEntity,
    long? AffectedEntityId,
    string? IpAddress,
    DateTimeOffset CreatedAtUtc,
    string CreatedAtLocal);
