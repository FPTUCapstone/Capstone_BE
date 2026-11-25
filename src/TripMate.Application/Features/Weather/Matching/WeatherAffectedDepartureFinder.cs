using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Weather.Emergency;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Weather.Matching;

public sealed class WeatherAffectedDepartureFinder(
    IApplicationDbContext context,
    IWeatherDepartureImpactLoader impactLoader) : IWeatherAffectedDepartureFinder
{
    public async Task<IReadOnlyList<WeatherDepartureCandidate>> FindAffectedDeparturesAsync(
        NormalizedWeatherEvent weatherEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(weatherEvent);
        if (!weatherEvent.HasSpatialScope)
        {
            return [];
        }

        var schedules = await context.TourSchedules
            .Include(s => s.Tour)
                .ThenInclude(t => t.ItineraryItems)
                    .ThenInclude(i => i.Poi)
            .Where(s => s.Status == TourScheduleStatus.Scheduled &&
                        s.StartAtUtc < weatherEvent.ValidToUtc &&
                        s.EndAtUtc > weatherEvent.ValidFromUtc)
            .ToListAsync(cancellationToken);

        if (schedules.Count == 0)
        {
            return [];
        }

        var passedPoisBySchedule = await LoadPassedPoisByScheduleAsync(schedules, cancellationToken);

        var affected = new List<WeatherDepartureCandidate>();
        foreach (var schedule in schedules)
        {
            passedPoisBySchedule.TryGetValue(schedule.Id, out var passedPois);
            var impact = impactLoader.LoadImpact(schedule, passedPois);
            bool intersects = (impact.MeetingPoint is { } meeting && weatherEvent.Covers(meeting))
                || impact.RequiredStops.Any(stop => weatherEvent.Covers(stop.Point));
            if (intersects)
            {
                affected.Add(new WeatherDepartureCandidate(schedule, impact));
            }
        }

        return affected;
    }

    private async Task<IReadOnlyDictionary<long, IReadOnlySet<long>>> LoadPassedPoisByScheduleAsync(
        IReadOnlyCollection<Domain.Entities.TourSchedule> schedules,
        CancellationToken cancellationToken)
    {
        var scheduleIds = schedules.Select(s => s.Id).ToList();
        var bookings = await context.Bookings.AsNoTracking()
            .Where(b => scheduleIds.Contains(b.TourScheduleId)
                && (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.PendingPayment))
            .Select(b => new { b.Id, b.TourScheduleId })
            .ToListAsync(cancellationToken);

        if (bookings.Count == 0)
        {
            return new Dictionary<long, IReadOnlySet<long>>();
        }

        var bookingToSchedule = bookings.ToDictionary(b => b.Id, b => b.TourScheduleId);
        var bookingIds = bookingToSchedule.Keys.ToList();

        var activeSessions = await context.TripSessions.AsNoTracking()
            .Include(s => s.Items)
            .Include(s => s.Itinerary)
            .Where(s => s.Itinerary.SourceBookingId != null
                && bookingIds.Contains(s.Itinerary.SourceBookingId.Value)
                && s.EndedAtUtc == null
                && s.FsmState != Domain.Entities.TripSession.CompletedState
                && s.FsmState != Domain.Entities.TripSession.InterruptedState)
            .ToListAsync(cancellationToken);

        var result = new Dictionary<long, IReadOnlySet<long>>();
        foreach (var group in activeSessions.GroupBy(s => bookingToSchedule[s.Itinerary.SourceBookingId!.Value]))
        {
            var sessions = group.ToList();
            var poiIds = sessions.SelectMany(s => s.Items.Select(i => i.PoiId)).Distinct();
            var passed = new HashSet<long>();

            foreach (var poiId in poiIds)
            {
                bool allFinishedPoi = sessions.All(session =>
                {
                    var items = session.Items.Where(i => i.PoiId == poiId).ToList();
                    return items.Count > 0 && items.All(item =>
                        item.SkippedAtUtc.HasValue
                        || (item.ReachedAtUtc.HasValue
                            && (session.FsmState != Domain.Entities.TripSession.ExploringState
                                || session.ExploringItemId != item.ItineraryItemId)));
                });

                if (allFinishedPoi)
                {
                    passed.Add(poiId);
                }
            }

            if (passed.Count > 0)
            {
                result[group.Key] = passed;
            }
        }

        return result;
    }
}