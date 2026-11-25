using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Rerouting.Engine;

/// <summary>
/// Spec 4.2. The Backend never has the Traveler's GPS position, so the remaining route is anchored on stop data:
/// where the Traveler last was (the stop being explored, else the last reached stop, else the trip start), when
/// they can leave, and how much of the original day and budget is left.
/// </summary>
internal static class ReroutingAnchorFactory
{
    public static EvaluationAnchor Create(
        TripSession session,
        SchedulingRequest request,
        TripSessionItem affected,
        IReadOnlyDictionary<long, decimal?> costByItineraryItemId,
        decimal searchRadiusKm,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(affected);
        ArgumentNullException.ThrowIfNull(costByItineraryItemId);

        var exploring = session.ExploringItemId.HasValue
            ? session.Items.SingleOrDefault(item => item.ItineraryItemId == session.ExploringItemId.Value)
            : null;
        var lastReached = exploring
            ?? session.Items
                .Where(item => item.ReachedAtUtc.HasValue)
                .OrderByDescending(item => item.ReachedAtUtc)
                .FirstOrDefault();
        var start = lastReached is null
            ? new RoutePoint(request.StartLatitude, request.StartLongitude)
            : new RoutePoint(lastReached.Latitude, lastReached.Longitude);

        // A Traveler still at a stop cannot leave before its planned departure; an overstaying one leaves now.
        var startAt = exploring is not null && exploring.PlannedDepartureUtc > nowUtc
            ? exploring.PlannedDepartureUtc
            : nowUtc;

        var dayEnd = request.StartAtUtc.AddMinutes(request.AvailableMinutes);
        var minutesLeft = (int)Math.Floor((dayEnd - startAt).TotalMinutes);
        var availableMinutes = Math.Clamp(minutesLeft, 0, SchedulingRequest.MaxAvailableMinutes);

        decimal? remainingBudget = null;
        if (request.BudgetVnd.HasValue)
        {
            var spent = session.Items
                .Where(item => item.Status == TripSessionItem.ReachedStatus)
                .Sum(item => costByItineraryItemId.GetValueOrDefault(item.ItineraryItemId) ?? 0m);
            remainingBudget = Math.Max(0m, request.BudgetVnd.Value - spent);
        }

        return new EvaluationAnchor(
            startAt,
            start,
            availableMinutes,
            remainingBudget,
            searchRadiusKm,
            new RoutePoint(affected.Latitude, affected.Longitude));
    }
}