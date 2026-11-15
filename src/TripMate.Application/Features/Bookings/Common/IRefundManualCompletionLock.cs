namespace TripMate.Application.Features.Bookings.Common;

/// <summary>
/// Serializes manual-settlement decisions for one refund for the lifetime of the surrounding
/// database transaction. SQL Server implementations use transaction-owned sp_getapplock.
/// </summary>
public interface IRefundManualCompletionLock
{
    Task AcquireAsync(long refundId, CancellationToken cancellationToken);
}