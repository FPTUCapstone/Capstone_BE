using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Navigation.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Navigation.Start;

public sealed class StartNavigationSessionCommandHandler(
    IApplicationDbContext dbContext,
    IItineraryAccessService itineraryAccessService,
    IDateTimeProvider clock,
    NavigationSessionOptions? options = null)
    : IRequestHandler<StartNavigationSessionCommand, Result<NavigationSessionResponse>>
{
    private const int MaxAttempts = 3;

    private readonly NavigationSessionOptions _options = options ?? new NavigationSessionOptions();

    public async Task<Result<NavigationSessionResponse>> Handle(
        StartNavigationSessionCommand request,
        CancellationToken cancellationToken)
    {
        // A concurrent start that wins the idempotency-key or open-session unique index, or a
        // deadlock, re-runs the whole attempt: the retry then replays the winner or reports the
        // open session, with authorization re-evaluated.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var attemptResult = await dbContext.ExecuteInTransactionAsync(
                    transactionCancellationToken => StartAttemptAsync(request, transactionCancellationToken),
                    cancellationToken);
                NavigationSessionMetrics.RecordStart(attemptResult.IsReplay
                    ? NavigationSessionMetrics.ReplayedOutcome
                    : attemptResult.Result.IsSuccess
                        ? NavigationSessionMetrics.AcceptedOutcome
                        : NavigationSessionMetrics.RejectedOutcome);
                return attemptResult.Result;
            }
            catch (Exception exception) when (attempt < MaxAttempts
                && (exception is DbUpdateException || dbContext.IsDeadlock(exception)))
            {
                dbContext.ClearTrackedEntities();
            }
        }
    }

    private async Task<StartAttemptResult> StartAttemptAsync(
        StartNavigationSessionCommand request,
        CancellationToken cancellationToken)
    {
        var replay = await LoadByKeyAsync(
            request.TravelerUserId,
            request.IdempotencyKey,
            cancellationToken);
        return replay is not null
            ? new StartAttemptResult(await ReplayAsync(replay, request, cancellationToken), IsReplay: true)
            : new StartAttemptResult(await StartAsync(request, cancellationToken), IsReplay: false);
    }

    private async Task<Result<NavigationSessionResponse>> StartAsync(
        StartNavigationSessionCommand request,
        CancellationToken cancellationToken)
    {
        // Held until the commit, so a membership revocation cannot slip between this check and
        // the new session becoming visible.
        var access = await dbContext.ExecuteWithSerializableReadsAsync(
            readCancellationToken => itineraryAccessService.ResolveCurrentAsync(
                request.ItineraryId,
                request.TravelerUserId,
                trackCurrent: false,
                readCancellationToken),
            cancellationToken);
        if (access.IsFailure)
        {
            return access.ErrorCode == ItineraryErrorCodes.NotFound
                ? Result.Failure<NavigationSessionResponse>(access.ErrorCode, access.ErrorMessage!)
                : AccessDenied();
        }

        var itinerary = await dbContext.Itineraries
            .AsNoTracking()
            .Include(candidate => candidate.SchedulingRequest)
            .Include(candidate => candidate.Items)
            .ThenInclude(item => item.PointOfInterest)
            .SingleAsync(candidate => candidate.Id == access.Value.CurrentItinerary.Id, cancellationToken);
        if (itinerary.Status != Itinerary.ActiveStatus)
        {
            return Result.Failure<NavigationSessionResponse>(
                NavigationErrorCodes.ItineraryNotActive,
                "Navigation requires an Active itinerary.");
        }

        var navigableItems = itinerary.Items
            .Where(item => item.PointOfInterest is not null)
            .OrderBy(item => item.SequenceNo)
            .ToArray();
        if (navigableItems.Length == 0)
        {
            return Result.Failure<NavigationSessionResponse>(
                NavigationErrorCodes.NoNavigableItems,
                "The itinerary has no navigable items.");
        }

        // The schedule drives the trip window and expiry, so a POI is never silently dropped.
        if (navigableItems.Any(item => item.PlannedArrivalUtc is null || item.PlannedDepartureUtc is null))
        {
            return Result.Failure<NavigationSessionResponse>(
                NavigationErrorCodes.ItineraryScheduleIncomplete,
                "Every navigable itinerary item needs a planned arrival and departure.");
        }

        var snapshot = navigableItems
            .Select(item => TripSessionItem.Snapshot(
                item.Id,
                item.SequenceNo,
                item.PointOfInterestId!.Value,
                item.PointOfInterest!.Name,
                item.PointOfInterest.Latitude,
                item.PointOfInterest.Longitude,
                item.PlannedArrivalUtc!.Value,
                item.PlannedDepartureUtc!.Value,
                item.IsMandatory))
            .ToArray();

        var now = clock.UtcNow;
        // The window opens before the planned departure, not before the first stop: a trip that
        // leaves at 01:46 to reach a place opening at 07:00 must be navigable from 01:46.
        // Manual itineraries have no departure time and fall back to the first stop.
        var firstArrivalUtc = snapshot.Min(item => item.PlannedArrivalUtc);
        var departureUtc = itinerary.ValidFromUtc is { } validFrom && validFrom < firstArrivalUtc
            ? validFrom
            : firstArrivalUtc;
        var windowStartUtc = departureUtc - _options.EarlyStartWindow;
        var expiresAtUtc = snapshot.Max(item => item.PlannedDepartureUtc) + _options.ExpiryGracePeriod;
        if (now < windowStartUtc || now >= expiresAtUtc)
        {
            return Result.Failure<NavigationSessionResponse>(
                NavigationErrorCodes.OutsideTripWindow,
                "Navigation can start only within the itinerary's trip time window.");
        }

        var open = await dbContext.TripSessions
            .Include(session => session.Items)
            .SingleOrDefaultAsync(
                session => session.TravelerUserId == request.TravelerUserId && session.EndedAtUtc == null,
                cancellationToken);
        if (open is not null)
        {
            if (!open.ExpireIfDue(now))
            {
                return ActiveSessionExists(open.Id);
            }

            dbContext.MarkTripSessionProgressForConcurrencyCheck(open);
            await dbContext.SaveChangesAsync(cancellationToken);
            NavigationSessionMetrics.RecordExpiration(NavigationSessionMetrics.AcceptedOutcome);
        }

        var session = TripSession.Start(
            itinerary.Id,
            request.ItineraryId,
            request.TravelerUserId,
            request.IdempotencyKey,
            now,
            expiresAtUtc,
            snapshot);
        dbContext.TripSessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success(NavigationSessionResponse.From(session, itinerary));
    }

    private async Task<Result<NavigationSessionResponse>> ReplayAsync(
        TripSession replay,
        StartNavigationSessionCommand request,
        CancellationToken cancellationToken)
    {
        if (replay.RequestedItineraryId != request.ItineraryId)
        {
            return Result.Failure<NavigationSessionResponse>(
                NavigationErrorCodes.IdempotencyKeyPayloadMismatch,
                "The Idempotency-Key was already used for another itinerary.");
        }

        var access = await itineraryAccessService.ResolveCurrentAsync(
            replay.RequestedItineraryId,
            request.TravelerUserId,
            trackCurrent: false,
            cancellationToken);
        if (access.IsFailure)
        {
            return AccessDenied();
        }

        // Untracked: shows the effective state without persisting a read-time expiry.
        replay.ExpireIfDue(clock.UtcNow);
        return Result.Success(NavigationSessionResponse.From(replay, replay.Itinerary));
    }

    private async Task<TripSession?> LoadByKeyAsync(
        long travelerUserId,
        Guid key,
        CancellationToken cancellationToken) =>
        await dbContext.TripSessions
            .AsNoTracking()
            .Include(session => session.Itinerary)
            .ThenInclude(itinerary => itinerary.SchedulingRequest)
            .Include(session => session.Items)
            .SingleOrDefaultAsync(
                session => session.TravelerUserId == travelerUserId
                    && session.StartIdempotencyKey == key,
                cancellationToken);

    private static Result<NavigationSessionResponse> AccessDenied() =>
        Result.Failure<NavigationSessionResponse>(
            NavigationErrorCodes.AccessDenied,
            "You do not have access to this navigation session.");

    private static Result<NavigationSessionResponse> ActiveSessionExists(long sessionId)
    {
        var location = $"/api/v1/navigation-sessions/{sessionId}";
        return Result.Failure<NavigationSessionResponse>(
            NavigationErrorCodes.ActiveSessionExists,
            "An active navigation session already exists.",
            new Dictionary<string, object?>
            {
                [NavigationErrorMetadata.ActiveSessionId] = sessionId,
                [NavigationErrorMetadata.ActiveSessionLocation] = location,
            });
    }

    private sealed record StartAttemptResult(
        Result<NavigationSessionResponse> Result,
        bool IsReplay);
}