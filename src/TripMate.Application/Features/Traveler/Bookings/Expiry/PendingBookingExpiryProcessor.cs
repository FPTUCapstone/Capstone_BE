using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Bookings.Expiry;

/// <summary>
/// Expires payment holds and returns their schedule capacity. The worker processes each booking
/// in an isolated serializable transaction so one corrupt historic row cannot block other due
/// holds. Booking creation uses the Traveler-scoped method inside its own transaction.
/// </summary>
public sealed class PendingBookingExpiryProcessor(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock,
    TourBookingOptions options)
{
    public async Task<PendingBookingExpirySweepResult> ExpireDueBookingsAsync(
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.UtcNow;
        List<long> bookingIds = await dbContext.Bookings.AsNoTracking()
            .Where(booking => booking.Status == BookingStatus.PendingPayment &&
                              booking.PaymentExpiresAtUtc.HasValue &&
                              booking.PaymentExpiresAtUtc.Value <= now)
            .OrderBy(booking => booking.Id)
            .Select(booking => booking.Id)
            .Take(options.ExpirySweepBatchSize)
            .ToListAsync(cancellationToken);

        var expiredCount = 0;
        var failures = new List<PendingBookingExpiryFailure>();
        foreach (long bookingId in bookingIds)
        {
            try
            {
                expiredCount += await dbContext.ExecuteInSerializableTransactionAsync(
                    transactionCancellationToken => ExpireDueBookingInCurrentTransactionAsync(
                        bookingId, transactionCancellationToken),
                    cancellationToken);
            }
            catch (Exception exception)
            {
                // A failed transaction can leave Added/Modified state tracked. Clear it before
                // continuing with independent due bookings in this worker scope.
                dbContext.ClearTrackedEntities();
                failures.Add(new PendingBookingExpiryFailure(bookingId, exception.GetType().Name));
            }
        }

        return new PendingBookingExpirySweepResult(expiredCount, failures);
    }

    public Task<int> ExpireDueBookingsForTravelerInCurrentTransactionAsync(
        long travelerUserId,
        CancellationToken cancellationToken)
    {
        if (travelerUserId <= 0) throw new ArgumentOutOfRangeException(nameof(travelerUserId));
        return ExpireDueBookingsInCurrentTransactionAsync(cancellationToken, travelerUserId);
    }

    private async Task<int> ExpireDueBookingInCurrentTransactionAsync(
        long bookingId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.UtcNow;
        Booking? booking = await dbContext.Bookings
            .Include(candidate => candidate.TourSchedule)
            .SingleOrDefaultAsync(candidate => candidate.Id == bookingId &&
                                        candidate.Status == BookingStatus.PendingPayment &&
                                        candidate.PaymentExpiresAtUtc.HasValue &&
                                        candidate.PaymentExpiresAtUtc.Value <= now,
                cancellationToken);
        if (booking is null || !booking.ExpirePendingPayment(now)) return 0;

        booking.TourSchedule.ReleaseCapacity(booking.Quantity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return 1;
    }

    private async Task<int> ExpireDueBookingsInCurrentTransactionAsync(
        CancellationToken cancellationToken,
        long travelerUserId)
    {
        DateTimeOffset now = clock.UtcNow;
        List<Booking> bookings = await dbContext.Bookings
            .Include(booking => booking.TourSchedule)
            .Where(booking => booking.TravelerUserId == travelerUserId &&
                              booking.Status == BookingStatus.PendingPayment &&
                              booking.PaymentExpiresAtUtc.HasValue &&
                              booking.PaymentExpiresAtUtc.Value <= now)
            .ToListAsync(cancellationToken);

        var expiredCount = 0;
        foreach (Booking booking in bookings)
        {
            if (!booking.ExpirePendingPayment(now)) continue;
            booking.TourSchedule.ReleaseCapacity(booking.Quantity);
            expiredCount++;
        }

        if (expiredCount > 0)
            await dbContext.SaveChangesAsync(cancellationToken);
        return expiredCount;
    }
}

public sealed record PendingBookingExpirySweepResult(
    int ExpiredCount,
    IReadOnlyList<PendingBookingExpiryFailure> Failures);

public sealed record PendingBookingExpiryFailure(long BookingId, string ExceptionType);