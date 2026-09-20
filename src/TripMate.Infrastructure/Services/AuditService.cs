using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Services;

public sealed class AuditService(
    IApplicationDbContext dbContext,
    ILogger<AuditService> logger) : IAuditService
{
    public async Task<int> DeleteExpiredSignOutAuditEventsAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.DeleteSignOutAuditEventsBeforeAsync(
                cutoffUtc,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "AUDIT_CLEANUP_FAILURE: Failed to delete expired sign-out audit events.");
            throw;
        }
    }
}