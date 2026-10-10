using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Navigation.Common;

/// <summary>
/// Shared pipeline for every navigation-session mutation: ownership and access checks, one
/// transaction, lazy expiry before the action, the row-version fence, and a bounded retry that
/// re-applies the action to fresh state after an optimistic-concurrency conflict.
/// </summary>
internal sealed class NavigationSessionMutator(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock)
{
    private const int MaxAttempts = 3;

    public async Task<Result<NavigationSessionResponse>> ExecuteAsync(
        long sessionId,
        long travelerUserId,
        IItineraryAccessService? requiredItineraryAccess,
        Func<TripSession, DateTimeOffset, TripSessionProgressOutcome> mutate,
        Action<string> recordOutcome,
        CancellationToken cancellationToken)
    {
        var identity = await dbContext.TripSessions
            .AsNoTracking()
            .Where(session => session.Id == sessionId)
            .Select(session => new { session.TravelerUserId, session.RequestedItineraryId })
            .SingleOrDefaultAsync(cancellationToken);
        if (identity is null)
        {
            recordOutcome(NavigationSessionMetrics.RejectedOutcome);
            return NotFound();
        }

        if (identity.TravelerUserId != travelerUserId)
        {
            recordOutcome(NavigationSessionMetrics.RejectedOutcome);
            return AccessDenied();
        }

        if (requiredItineraryAccess is not null)
        {
            var access = await requiredItineraryAccess.ResolveCurrentAsync(
                identity.RequestedItineraryId,
                travelerUserId,
                trackCurrent: false,
                cancellationToken);
            if (access.IsFailure)
            {
                recordOutcome(NavigationSessionMetrics.RejectedOutcome);
                return AccessDenied();
            }
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var outcome = await dbContext.ExecuteInTransactionAsync(
                    transactionCancellationToken => MutateAsync(sessionId, mutate, transactionCancellationToken),
                    cancellationToken);
                if (outcome.Expired)
                {
                    NavigationSessionMetrics.RecordExpiration(NavigationSessionMetrics.AcceptedOutcome);
                }

                recordOutcome(outcome.MetricOutcome);
                return outcome.Result;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                dbContext.ClearTrackedEntities();
            }
        }
    }

    private async Task<MutationOutcome> MutateAsync(
        long sessionId,
        Func<TripSession, DateTimeOffset, TripSessionProgressOutcome> mutate,
        CancellationToken cancellationToken)
    {
        var session = await dbContext.TripSessions
            .Include(candidate => candidate.Itinerary)
            .ThenInclude(itinerary => itinerary.SchedulingRequest)
            .Include(candidate => candidate.Items)
            .Include(candidate => candidate.StateHistory)
            .SingleOrDefaultAsync(candidate => candidate.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return new MutationOutcome(NotFound(), NavigationSessionMetrics.RejectedOutcome, Expired: false);
        }

        var now = clock.UtcNow;
        var expired = session.ExpireIfDue(now);
        var progress = mutate(session, now);
        if (expired || progress == TripSessionProgressOutcome.Applied)
        {
            dbContext.MarkTripSessionProgressForConcurrencyCheck(session);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return progress switch
        {
            TripSessionProgressOutcome.Applied => Success(session, NavigationSessionMetrics.AcceptedOutcome, expired),
            TripSessionProgressOutcome.Replayed => Success(session, NavigationSessionMetrics.ReplayedOutcome, expired),
            TripSessionProgressOutcome.NotInSnapshot => Failure(
                NavigationErrorCodes.ItemNotNavigable,
                "The item is not part of the navigation snapshot.",
                expired),
            TripSessionProgressOutcome.AlreadyReached => Failure(
                NavigationErrorCodes.ItemAlreadyReached,
                "A reached navigation item cannot be skipped.",
                expired),
            TripSessionProgressOutcome.SessionCompleted => Failure(
                NavigationErrorCodes.SessionCompleted,
                "The navigation session is already completed.",
                expired),
            _ => throw new InvalidOperationException($"Unsupported navigation progress outcome '{progress}'."),
        };
    }

    private static MutationOutcome Success(TripSession session, string metricOutcome, bool expired) =>
        new(
            Result.Success(NavigationSessionResponse.From(session, session.Itinerary)),
            metricOutcome,
            expired);

    private static MutationOutcome Failure(string errorCode, string message, bool expired) =>
        new(
            Result.Failure<NavigationSessionResponse>(errorCode, message),
            NavigationSessionMetrics.RejectedOutcome,
            expired);

    private static Result<NavigationSessionResponse> NotFound() =>
        Result.Failure<NavigationSessionResponse>(
            NavigationErrorCodes.SessionNotFound,
            "The navigation session was not found.");

    private static Result<NavigationSessionResponse> AccessDenied() =>
        Result.Failure<NavigationSessionResponse>(
            NavigationErrorCodes.AccessDenied,
            "You do not have access to this navigation session.");

    private sealed record MutationOutcome(
        Result<NavigationSessionResponse> Result,
        string MetricOutcome,
        bool Expired);
}