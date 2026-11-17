using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.ServiceProviderRequests;

public sealed class ServiceRequestLifecycleProcessor(IApplicationDbContext db,
    IServiceProviderRequestLock mutationLock, IDateTimeProvider clock)
{
    public async Task<int> ProcessAsync(CancellationToken token)
    {
        var now = clock.UtcNow;
        var responseCutoff = now.AddHours(-24);
        var today = DateOnly.FromDateTime(now.ToOffset(ServiceBooking.VietnamOffset).DateTime);
        // Three full Vietnam dates following the requested date must have elapsed.
        var serviceCutoff = new DateTimeOffset(today.AddDays(-3).ToDateTime(TimeOnly.MinValue),
            ServiceBooking.VietnamOffset).ToUniversalTime();
        var candidates = await db.ServiceBookings.AsNoTracking()
            .Where(booking => booking.RequestSubmittedAtUtc != null
                && ((booking.Status == ServiceBooking.PendingConfirmation && booking.RequestSubmittedAtUtc <= responseCutoff)
                    || (booking.Status == ServiceBooking.Confirmed && booking.StartAtUtc < serviceCutoff)))
            .OrderBy(booking => booking.Id).Select(booking => booking.Id).Take(100).ToListAsync(token);

        var processed = 0;
        foreach (var id in candidates)
        {
            try
            {
                var changed = await db.ExecuteInTransactionAsync(async transactionToken =>
                {
                    db.ClearTrackedEntities();
                    await mutationLock.AcquireBookingAsync(id, transactionToken);
                    var booking = await db.ServiceBookings.SingleOrDefaultAsync(candidate => candidate.Id == id, transactionToken);
                    if (booking is null) return false;
                    var before = booking.Status;
                    var transactionNow = clock.UtcNow;
                    if (!booking.TryExpire(transactionNow) && !booking.TryDefaultComplete(transactionNow)) return false;
                    ProviderRequestEvents.Record(db, booking, null, before, transactionNow);
                    await db.SaveChangesAsync(transactionToken);
                    return true;
                }, token);
                if (changed) processed++;
            }
            catch (DbUpdateConcurrencyException)
            {
                // A concurrent writer committed first. Reconsider this row on the next pass.
                db.ClearTrackedEntities();
            }
            catch
            {
                db.ClearTrackedEntities();
                throw;
            }
        }
        return processed;
    }
}