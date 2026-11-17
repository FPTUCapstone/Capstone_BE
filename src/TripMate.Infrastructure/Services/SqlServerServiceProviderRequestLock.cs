using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.ServiceProviderRequests;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.Services;

public sealed class SqlServerServiceProviderRequestLock(ApplicationDbContext db) : IServiceProviderRequestLock
{
    public Task AcquireOperationAsync(long actorUserId, Guid key, CancellationToken cancellationToken) =>
        AcquireAsync($"TripMate:ServiceRequestOperation:{actorUserId}:{key:D}", cancellationToken);

    public Task AcquireBookingAsync(long bookingId, CancellationToken cancellationToken) =>
        AcquireAsync($"TripMate:ServiceRequestBooking:{bookingId}", cancellationToken);

    private async Task AcquireAsync(string resource, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Service request locks require a caller-owned transaction.");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @lockResult INT;
            EXEC @lockResult = sys.sp_getapplock
                @Resource = {resource}, @LockMode = 'Exclusive',
                @LockOwner = 'Transaction', @LockTimeout = 15000;
            IF @lockResult < 0 THROW 51004, 'Could not acquire the service request lock.', 1;
            """, token);
    }
}