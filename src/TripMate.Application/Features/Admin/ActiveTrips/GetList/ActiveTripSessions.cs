using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Admin.ActiveTrips.GetList;

/// <summary>
/// The one effective-activity rule shared by UC-58 (list) and UC-59 (detail): an open FSM state
/// whose lazy expiry has not passed. It mirrors <see cref="TripSession.ExpireIfDue"/>, which
/// closes a session once the clock reaches <c>ExpiresAtUtc</c>, so a session that has expired but
/// was never touched again is absent from both screens. Legacy sessions without an expiry stay
/// active.
/// </summary>
public static class ActiveTripSessions
{
    private static readonly string[] ActiveStates =
        [TripSession.NavigatingState, TripSession.ExploringState, TripSession.InterruptedState];

    public static IQueryable<TripSession> WhereEffectivelyActive(
        this IQueryable<TripSession> sessions,
        DateTimeOffset nowUtc) =>
        sessions.Where(session => ActiveStates.Contains(session.FsmState)
            && (session.ExpiresAtUtc == null || session.ExpiresAtUtc > nowUtc));
}