using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Rerouting.Common;

internal enum ReroutingGate
{
    /// <summary>The proposal is open: carry out the decision.</summary>
    Proceed,

    /// <summary>The proposal already holds this very decision: succeed again without changing anything (double tap).</summary>
    Replay,

    /// <summary>The proposal was closed the other way (accepted vs rejected).</summary>
    Closed,

    /// <summary>The proposal is, or has just been marked, Expired. The caller must save the change.</summary>
    Expired,
}

/// <summary>
/// The checks shared by Accept and Reject (spec 2.2, 6 and 7): the decision itself, the lazy expiry of the session
/// and the lazy TTL check, evaluated at the moment of the call and never left to the sweep.
/// </summary>
internal static class ReroutingDecisionGate
{
    public const string Message = "No re-routing suggestion is available at the moment.";

    public static ReroutingGate Evaluate(
        ReroutingEvent proposal,
        TripSession session,
        string targetStatus,
        DateTimeOffset nowUtc,
        TimeSpan ttl)
    {
        if (proposal.Status == targetStatus)
        {
            return ReroutingGate.Replay;
        }

        if (proposal.Status == ReroutingEvent.StatusExpired)
        {
            return ReroutingGate.Expired;
        }

        if (proposal.Status != ReroutingEvent.StatusProposed)
        {
            return ReroutingGate.Closed;
        }

        session.ExpireIfDue(nowUtc);
        var ended = session.EndedAtUtc.HasValue
            || session.FsmState is TripSession.CompletedState or TripSession.InterruptedState;
        if (ended || proposal.IsExpiredAt(nowUtc, ttl))
        {
            proposal.Expire(nowUtc);
            return ReroutingGate.Expired;
        }

        return ReroutingGate.Proceed;
    }

    public static Result<T> NotFound<T>() =>
        Result.Failure<T>(ReroutingErrorCodes.NotFound, Message);

    public static Result<T> Expired<T>() =>
        Result.Failure<T>(ReroutingErrorCodes.ProposalExpired, Message);

    public static Result<T> Closed<T>() =>
        Result.Failure<T>(ReroutingErrorCodes.ProposalClosed, Message);

    public static Result<T> Infeasible<T>() =>
        Result.Failure<T>(ReroutingErrorCodes.ProposalInfeasible, Message);
}