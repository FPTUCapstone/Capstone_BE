using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.CheckIn.Audit;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Infrastructure.Services.Audit;

internal enum CheckInAuditProjectionResult { Projected, Duplicate, Conflict }

internal sealed class CheckInAuditProjector(IApplicationDbContext dbContext)
{
    public async Task<CheckInAuditProjectionResult> ProjectAsync(CheckInAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        if (auditEvent.EventType != CheckInAuditEventType.Rejected)
            throw new InvalidOperationException("Only rejection outcomes may be projected to SQL audit rows.");

        AuditLog? existing = await FindAsync(auditEvent.EventId, cancellationToken);
        if (existing is not null) return IsSame(existing, auditEvent) ? CheckInAuditProjectionResult.Duplicate : CheckInAuditProjectionResult.Conflict;

        dbContext.AuditLogs.Add(AuditLog.CreateCheckInJournalProjection(
            auditEvent.ActorUserId, auditEvent.ScheduleId, auditEvent.TicketId, auditEvent.ScanAttemptId,
            auditEvent.EventId, auditEvent.ReasonCode!, auditEvent.OccurredAtUtc));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return CheckInAuditProjectionResult.Projected;
        }
        catch (DbUpdateException)
        {
            dbContext.ClearTrackedEntities();
            existing = await FindAsync(auditEvent.EventId, cancellationToken);
            if (existing is null) throw;
            return IsSame(existing, auditEvent) ? CheckInAuditProjectionResult.Duplicate : CheckInAuditProjectionResult.Conflict;
        }
    }

    private Task<AuditLog?> FindAsync(Guid eventId, CancellationToken cancellationToken) => dbContext.AuditLogs.AsNoTracking()
        .SingleOrDefaultAsync(row => row.JournalEventId == eventId, cancellationToken);

    internal static bool IsSame(AuditLog row, CheckInAuditEvent auditEvent) =>
        row.JournalEventId == auditEvent.EventId && row.ScanAttemptId == auditEvent.ScanAttemptId &&
        row.ActorUserId == auditEvent.ActorUserId && row.AffectedEntityId == auditEvent.TicketId &&
        row.ActionType == TripMate.Domain.Common.AuditActionTypes.CheckInScanRejected &&
        row.AffectedEntity == TripMate.Domain.Common.AuditEntityTypes.CheckIn &&
        row.Result == AuditOutcome.Failure && row.Reason == auditEvent.ReasonCode &&
        row.CreatedAtUtc == auditEvent.OccurredAtUtc &&
        HasSchedule(row.AfterData, auditEvent.ScheduleId);

    private static bool HasSchedule(string? projectionData, long scheduleId)
    {
        if (string.IsNullOrWhiteSpace(projectionData)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(projectionData);
            return document.RootElement.TryGetProperty("scheduleId", out JsonElement value)
                && value.TryGetInt64(out long persistedScheduleId)
                && persistedScheduleId == scheduleId;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}