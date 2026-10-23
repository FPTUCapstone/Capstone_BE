using TripMate.Domain.Entities;

namespace TripMate.Application.Features.TripHistory.Common;

/// <summary>
/// Spec TM-78 T1 and T4: which session represents an itinerary in Trip History, and the effective
/// (read-only) completion state of a session. The effective state mirrors
/// <see cref="TripSession.ExpireIfDue"/>, which closes a session lazily once the clock reaches
/// <c>ExpiresAtUtc</c>; nothing here writes.
/// </summary>
public static class TripSessionQueries
{
    /// <summary>
    /// The one session that represents an itinerary, per itinerary and per Traveler (A2): the latest
    /// effectively Completed one, i.e. <c>OrderByDescending(effective end).ThenByDescending(Id).First()</c>.
    /// The effective end is <c>EndedAtUtc</c> for a stored Completed session and <c>ExpiresAtUtc</c> for an
    /// open session past its expiry; a row with no end instant ranks below any row that has one, as
    /// descending order places nulls last. Written as a "no better row exists" predicate so it
    /// translates to SQL without a GroupBy. List, detail and summary all go through here so they cannot
    /// disagree.
    /// </summary>
    public static IQueryable<TripSession> RepresentativeCompletedSessions(
        this IQueryable<TripSession> sessions,
        long travelerUserId,
        DateTimeOffset nowUtc) =>
        sessions.Where(session =>
            session.TravelerUserId == travelerUserId
            && (session.FsmState == TripSession.CompletedState
                || (session.ExpiresAtUtc != null && session.ExpiresAtUtc <= nowUtc))
            && !sessions.Any(better =>
                better.TravelerUserId == travelerUserId
                && better.ItineraryId == session.ItineraryId
                && (better.FsmState == TripSession.CompletedState
                    || (better.ExpiresAtUtc != null && better.ExpiresAtUtc <= nowUtc))
                && (
                    // better has an end and session has none
                    ((better.FsmState == TripSession.CompletedState ? better.EndedAtUtc : better.ExpiresAtUtc) != null
                        && (session.FsmState == TripSession.CompletedState ? session.EndedAtUtc : session.ExpiresAtUtc) == null)
                    // both have an end and better's is later
                    || ((better.FsmState == TripSession.CompletedState ? better.EndedAtUtc : better.ExpiresAtUtc) != null
                        && (session.FsmState == TripSession.CompletedState ? session.EndedAtUtc : session.ExpiresAtUtc) != null
                        && (better.FsmState == TripSession.CompletedState ? better.EndedAtUtc : better.ExpiresAtUtc)
                            > (session.FsmState == TripSession.CompletedState ? session.EndedAtUtc : session.ExpiresAtUtc))
                    // same end (including both missing): the higher id wins
                    || ((better.FsmState == TripSession.CompletedState ? better.EndedAtUtc : better.ExpiresAtUtc)
                            == (session.FsmState == TripSession.CompletedState ? session.EndedAtUtc : session.ExpiresAtUtc)
                        && better.Id > session.Id))));

    /// <summary>True for a stored Completed session and for an open one at or past its expiry.</summary>
    public static bool IsEffectivelyCompleted(this TripSession session, DateTimeOffset nowUtc) =>
        session.FsmState == TripSession.CompletedState
        || (session.ExpiresAtUtc is { } expiresAtUtc && expiresAtUtc <= nowUtc);

    /// <summary>When the session ended: the stored end, or the expiry instant for an expired open session.</summary>
    public static DateTimeOffset? EffectiveEndedAtUtc(this TripSession session, DateTimeOffset nowUtc) =>
        session.FsmState == TripSession.CompletedState
            ? session.EndedAtUtc
            : session.IsEffectivelyCompleted(nowUtc) ? session.ExpiresAtUtc : null;

    /// <summary>Why the session ended: the stored reason, or <c>Expired</c> for an expired open session.</summary>
    public static string? EffectiveCompletionReason(this TripSession session, DateTimeOffset nowUtc) =>
        session.FsmState == TripSession.CompletedState
            ? session.CompletionReason
            : session.IsEffectivelyCompleted(nowUtc) ? TripSession.ExpiredReason : null;
}