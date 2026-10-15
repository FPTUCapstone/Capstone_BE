using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Itineraries.Common;

/// <summary>
/// A trip being navigated on the mobile app must not change underneath its Travelers: while any
/// member has an open navigation session on any version of the trip, regenerate and adjust are
/// rejected and the detail tells clients to hide those actions.
/// </summary>
internal static class ItineraryNavigationLock
{
    public const string Message =
        "This trip is being navigated on the TripMate app. Finish the trip on the app before changing it.";

    public static Task<bool> IsInProgressAsync(
        IApplicationDbContext dbContext,
        long schedulingRequestId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken) =>
        dbContext.TripSessions
            .AsNoTracking()
            .AnyAsync(session =>
                session.EndedAtUtc == null
                && (session.FsmState == TripSession.NavigatingState
                    || session.FsmState == TripSession.ExploringState)
                && (session.ExpiresAtUtc == null || session.ExpiresAtUtc > nowUtc)
                && session.Itinerary.SchedulingRequestId == schedulingRequestId,
                cancellationToken);
}