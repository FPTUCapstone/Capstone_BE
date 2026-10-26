using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Traveler.Bookings.Common;

public static class BookingExpiryEffects
{
    /// <summary>Applies one pending-to-expired transition and releases its held resources.</summary>
    public static async Task<bool> ExpireAsync(IApplicationDbContext dbContext, Booking booking,
        DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (!booking.ExpirePendingPayment(nowUtc))
            return false;

        TourSchedule? schedule = await dbContext.TourSchedules
            .SingleOrDefaultAsync(candidate => candidate.Id == booking.TourScheduleId, cancellationToken);
        schedule?.ReleaseCapacity(booking.Quantity);
        await BookingVoucherUsage.ReleaseAsync(dbContext, booking, cancellationToken);
        return true;
    }
}