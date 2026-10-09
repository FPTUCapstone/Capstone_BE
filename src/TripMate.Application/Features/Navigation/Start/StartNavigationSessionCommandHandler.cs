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
    private readonly NavigationSessionOptions _options = options ?? new NavigationSessionOptions();

    public async Task<Result<NavigationSessionResponse>> Handle(
        StartNavigationSessionCommand request,
        CancellationToken cancellationToken)
    {
        var replay = await LoadByKeyAsync(request.TravelerUserId, request.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            return await ReplayAsync(replay, request, cancellationToken);
        }

        try
        {
            var transactionResult = await dbContext.ExecuteInTransactionAsync(
                transactionCancellationToken => StartAsync(request, transactionCancellationToken),
                cancellationToken);
            NavigationSessionMetrics.RecordStart(
                transactionResult.IsSuccess
                    ? NavigationSessionMetrics.AcceptedOutcome
                    : NavigationSessionMetrics.RejectedOutcome);
            return transactionResult;
        }
        catch (DbUpdateException)
        {
            dbContext.ClearTrackedEntities();
            var winner = await LoadByKeyAsync(
                request.TravelerUserId,
                request.IdempotencyKey,
                cancellationToken);
            if (winner is not null)
            {
                return await ReplayAsync(winner, request, cancellationToken);
            }

            var activeId = await dbContext.TripSessions
                .AsNoTracking()
                .Where(session => session.TravelerUserId == request.TravelerUserId
                    && session.EndedAtUtc == null)
                .Select(session => (long?)session.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (activeId.HasValue)
            {
                NavigationSessionMetrics.RecordStart(NavigationSessionMetrics.RejectedOutcome);
                return ActiveSessionExists(activeId.Value);
            }

            throw;
        }
    }

    private async Task<Result<NavigationSessionResponse>> StartAsync(
        StartNavigationSessionCommand request,
        CancellationToken cancellationToken)
    {
        var access = await itineraryAccessService.ResolveCurrentAsync(
            request.ItineraryId,
            request.TravelerUserId,
            trackCurrent: false,
            cancellationToken);
        if (access.IsFailure)
        {
            return access.ErrorCode == ItineraryErrorCodes.NotFound
                ? Result.Failure<NavigationSessionResponse>(access.ErrorCode, access.ErrorMessage!)
                : AccessDenied();
        }

        var itinerary = await dbContext.Itineraries
            .AsNoTracking()
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
        var windowStartUtc = snapshot.Min(item => item.PlannedArrivalUtc) - _options.EarlyStartWindow;
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
        return Result.Success(NavigationSessionResponse.From(session, itinerary.Version));
    }

    private async Task<Result<NavigationSessionResponse>> ReplayAsync(
        TripSession replay,
        StartNavigationSessionCommand request,
        CancellationToken cancellationToken)
    {
        if (replay.RequestedItineraryId != request.ItineraryId)
        {
            NavigationSessionMetrics.RecordStart(NavigationSessionMetrics.RejectedOutcome);
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
            NavigationSessionMetrics.RecordStart(NavigationSessionMetrics.RejectedOutcome);
            return AccessDenied();
        }

        // Untracked: shows the effective state without persisting a read-time expiry.
        replay.ExpireIfDue(clock.UtcNow);
        NavigationSessionMetrics.RecordStart(NavigationSessionMetrics.ReplayedOutcome);
        return Result.Success(NavigationSessionResponse.From(replay, replay.Itinerary.Version));
    }

    private async Task<TripSession?> LoadByKeyAsync(
        long travelerUserId,
        Guid key,
        CancellationToken cancellationToken) =>
        await dbContext.TripSessions
            .AsNoTracking()
            .Include(session => session.Itinerary)
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
}