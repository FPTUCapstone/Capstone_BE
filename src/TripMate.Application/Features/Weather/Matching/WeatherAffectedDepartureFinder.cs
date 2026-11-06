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

        var affected = new List<WeatherDepartureCandidate>();
        foreach (var schedule in schedules)
        {
            var impact = impactLoader.LoadImpact(schedule);
            bool intersects = (impact.MeetingPoint is { } meeting && weatherEvent.Covers(meeting))
                || impact.RequiredStops.Any(stop => weatherEvent.Covers(stop.Point));
            if (intersects)
            {
                affected.Add(new WeatherDepartureCandidate(schedule, impact));
            }
        }

        return affected;
    }
}