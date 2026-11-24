using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Tours.Shared;

/// <summary>Set-based discovery preflight using the same timeline policy as booking.
/// Call before counting/paging; no per-card detail reads or capacity mutations.</summary>
public static class PublicTourBookability
{
    public static async Task<long[]> ReadScheduleIdsAsync(
        IQueryable<Tour> tours, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await tours.AsNoTracking().Select(tour => new
        {
            tour.TimeZoneId,
            Stops = tour.ItineraryItems.Select(stop => new BookedTourStopInput(
                stop.DayNo, stop.SequenceNo, stop.PoiId, stop.PlannedTime,
                stop.StayDurationMinutes, stop.Notes)).ToArray(),
            Schedules = tour.Schedules.Where(schedule =>
                schedule.Status == TourScheduleStatus.Scheduled
                && schedule.StartAtUtc > now
                && schedule.EndAtUtc > schedule.StartAtUtc
                && (!schedule.RegistrationDeadlineAtUtc.HasValue || now < schedule.RegistrationDeadlineAtUtc)
                && schedule.TotalCapacity > 0 && schedule.ReservedCapacity >= 0
                && schedule.ReservedCapacity < schedule.TotalCapacity)
                .Select(schedule => new { schedule.Id, schedule.StartAtUtc, schedule.EndAtUtc }).ToArray(),
        }).ToListAsync(cancellationToken);

        return rows.SelectMany(tour => tour.Schedules.Where(schedule =>
            BookedTourItineraryTimeline.Plan(tour.Stops, tour.TimeZoneId,
                schedule.StartAtUtc, schedule.EndAtUtc).Stops is not null)
            .Select(schedule => schedule.Id)).ToArray();
    }
}