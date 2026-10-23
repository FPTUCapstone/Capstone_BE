using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripHistory.List;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.TripHistory.Common;

/// <summary>
/// The self-planned half of Trip History (spec TM-78 P2, P3, P4, P5, T1, T4, A1): the Traveler's own
/// Manual and CSP-generated itineraries for one tab. A Completed trip is an itinerary with a
/// representative completed session; Upcoming and Cancelled are the newest non-Draft version of a trip
/// that has none. Reads keys only; cards are hydrated for the final page.
/// </summary>
public static class TripItineraryStream
{
    private static readonly string[] InScopeSources = [Itinerary.ManualSourceType, Itinerary.CspGeneratedSourceType];

    public static async Task<TripStreamWindow> ReadAsync(
        IApplicationDbContext dbContext,
        TripStreamFilter filter,
        DateTimeOffset nowUtc,
        int windowSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);
        var matching = ApplyRange(Rows(dbContext, filter, nowUtc), filter.Range);

        var totalCount = await matching.CountAsync(cancellationToken);
        if (totalCount == 0)
        {
            return TripStreamWindow.Empty;
        }

        var rows = await matching
            .OrderByDescending(row => row.DepartureAtUtc)
            .ThenByDescending(row => row.Id)
            .Take(windowSize)
            .ToListAsync(cancellationToken);

        return new TripStreamWindow(
            totalCount,
            rows.Select(row => new TripKey(TripRecordKinds.Itinerary, row.Id, row.DepartureAtUtc)).ToArray());
    }

    /// <summary>
    /// P9: the Reached items of the representative sessions of every Completed itinerary matching the
    /// filter's owner and date range (all pages, not just the current one). The filter's tab is ignored.
    /// </summary>
    public static async Task<int> CountVisitedPoisAsync(
        IApplicationDbContext dbContext,
        TripStreamFilter filter,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var completed = filter with { Tab = ListTravelerTripsQuery.TabCompleted };
        var itineraryIds = ApplyRange(Rows(dbContext, completed, nowUtc), filter.Range).Select(row => row.Id);

        return await dbContext.TripSessions.AsNoTracking()
            .RepresentativeCompletedSessions(filter.TravelerUserId, nowUtc)
            .Where(session => itineraryIds.Contains(session.ItineraryId))
            .SumAsync(session => session.Items.Count(item => item.ReachedAtUtc != null), cancellationToken);
    }

    private static IQueryable<DepartureRow> Rows(
        IApplicationDbContext dbContext,
        TripStreamFilter filter,
        DateTimeOffset nowUtc)
    {
        var travelerId = filter.TravelerUserId;
        var owned = dbContext.Itineraries.AsNoTracking().Where(itinerary =>
            itinerary.TravelerUserId == travelerId && InScopeSources.Contains(itinerary.SourceType));
        var representatives = dbContext.TripSessions.AsNoTracking()
            .RepresentativeCompletedSessions(travelerId, nowUtc);

        if (filter.Tab == ListTravelerTripsQuery.TabCompleted)
        {
            // P4: the version the session froze. P5 / classifier: session start, then ValidFrom, then CreatedAt.
            return from itinerary in owned
                   join session in representatives on itinerary.Id equals session.ItineraryId
                   select new DepartureRow
                   {
                       Id = itinerary.Id,
                       DepartureAtUtc = session.StartedAtUtc ?? itinerary.ValidFromUtc ?? itinerary.CreatedAtUtc,
                   };
        }

        var status = filter.Tab switch
        {
            ListTravelerTripsQuery.TabUpcoming => Itinerary.ActiveStatus,
            ListTravelerTripsQuery.TabCancelled => Itinerary.CancelledStatus,
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter.Tab, "The tab is not supported."),
        };
        var allItineraries = dbContext.Itineraries.AsNoTracking();

        return owned
            .Where(itinerary => itinerary.Status == status
                && !representatives.Any(session => session.ItineraryId == itinerary.Id)
                // A1: only the newest non-Draft version of a scheduling request is a trip.
                && (itinerary.SchedulingRequestId == null
                    || !allItineraries.Any(newer =>
                        newer.SchedulingRequestId == itinerary.SchedulingRequestId
                        && newer.Status != Itinerary.DraftStatus
                        && (newer.Version > itinerary.Version
                            || (newer.Version == itinerary.Version && newer.Id > itinerary.Id)))))
            .Select(itinerary => new DepartureRow
            {
                Id = itinerary.Id,
                DepartureAtUtc = itinerary.ValidFromUtc ?? itinerary.CreatedAtUtc,
            });
    }

    private static IQueryable<DepartureRow> ApplyRange(IQueryable<DepartureRow> rows, VietnamDayRange range)
    {
        if (range.FromUtc is { } from)
        {
            rows = rows.Where(row => row.DepartureAtUtc >= from);
        }

        if (range.ToUtcExclusive is { } until)
        {
            rows = rows.Where(row => row.DepartureAtUtc < until);
        }

        return rows;
    }

    private sealed class DepartureRow
    {
        public long Id { get; init; }

        public DateTimeOffset DepartureAtUtc { get; init; }
    }
}