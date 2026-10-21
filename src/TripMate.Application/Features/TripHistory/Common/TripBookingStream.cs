using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TripHistory.Common;

/// <summary>
/// The tour-booking half of Trip History: the Traveler's own <c>commerce.Bookings</c> for one tab,
/// dated by the schedule start (spec TM-78 P1, P5). Reads keys only; cards are hydrated for the
/// final page.
/// </summary>
public static class TripBookingStream
{
    public static async Task<TripStreamWindow> ReadAsync(
        IApplicationDbContext dbContext,
        TripStreamFilter filter,
        int windowSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);
        var matching = Filter(dbContext.Bookings.AsNoTracking(), filter);

        var totalCount = await matching.CountAsync(cancellationToken);
        if (totalCount == 0)
        {
            return TripStreamWindow.Empty;
        }

        var rows = await matching
            .OrderByDescending(booking => booking.TourSchedule.StartAtUtc)
            .ThenByDescending(booking => booking.Id)
            .Take(windowSize)
            .Select(booking => new { booking.Id, booking.TourSchedule.StartAtUtc })
            .ToListAsync(cancellationToken);

        return new TripStreamWindow(
            totalCount,
            rows.Select(row => new TripKey(TripRecordKinds.CommerceBooking, row.Id, row.StartAtUtc)).ToArray());
    }

    /// <summary>The bookings of the filter's owner whose stored status maps to the filter's tab.</summary>
    public static IQueryable<Booking> Filter(IQueryable<Booking> bookings, TripStreamFilter filter)
    {
        // Derived from the classifier so the status-to-tab mapping lives in exactly one place.
        var statuses = Enum.GetValues<BookingStatus>()
            .Where(status => TripHistoryClassifier.TabForBooking(status) == filter.Tab)
            .ToArray();
        if (statuses.Length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(filter), filter.Tab, "The tab is not supported.");
        }

        // commerce.Bookings.tour_schedule_id is nullable (a booking can be linked to an itinerary instead).
        // Such a row is not a tour booking: it has no tour or departure to show, and the list query's join
        // drops it, so the count must drop it too or totalCount would exceed the items. A NULL never
        // satisfies the comparison.
        var matching = bookings.Where(booking =>
            booking.TravelerUserId == filter.TravelerUserId
            && booking.TourScheduleId > 0
            && statuses.Contains(booking.Status));
        if (filter.Range.FromUtc is { } from)
        {
            matching = matching.Where(booking => booking.TourSchedule.StartAtUtc >= from);
        }

        if (filter.Range.ToUtcExclusive is { } until)
        {
            matching = matching.Where(booking => booking.TourSchedule.StartAtUtc < until);
        }

        return matching;
    }
}