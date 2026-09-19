using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Infrastructure.Services;

public sealed class AuditFailureRecorder(IServiceScopeFactory scopeFactory,
    IDateTimeProvider clock, ILogger<AuditFailureRecorder> logger) : IAuditFailureRecorder
{
    public async Task RecordAsync(AuditFailureEvent auditEvent)
    {
        try
        {
            // Never reuse the request context: its tracked entities may contain rolled-back writes.
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var metadata = JsonSerializer.Serialize(new { auditMetadata = new { errorCode = auditEvent.ErrorCode } });
            db.AuditLogs.Add(AuditLog.CreateRecordedOutcome(auditEvent.ActorUserId, auditEvent.ActionType,
                auditEvent.AffectedEntity, auditEvent.AffectedEntityId, clock.UtcNow, AuditOutcome.Failure,
                afterData: metadata));
            await db.SaveChangesAsync(timeout.Token);
        }
        catch (Exception)
        {
            // Keep original handler outcome; no submitted values or raw database errors in fallback.
            logger.LogError("Audit persistence unavailable: actor={ActorId}, action={Action}, entity={Entity}, entityId={EntityId}, errorCode={ErrorCode}",
                auditEvent.ActorUserId, auditEvent.ActionType, auditEvent.AffectedEntity,
                auditEvent.AffectedEntityId, auditEvent.ErrorCode);
        }
    }
}