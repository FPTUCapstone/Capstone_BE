namespace TripMate.Application.Common.Interfaces;

public interface IAuditService
{
    Task<int> DeleteExpiredSignOutAuditEventsAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default);
}