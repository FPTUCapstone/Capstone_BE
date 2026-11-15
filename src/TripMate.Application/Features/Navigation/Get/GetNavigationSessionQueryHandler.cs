using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Navigation.Common;

namespace TripMate.Application.Features.Navigation.Get;

public sealed class GetNavigationSessionQueryHandler(
    IApplicationDbContext dbContext,
    IItineraryAccessService itineraryAccessService,
    IDateTimeProvider clock)
    : IRequestHandler<GetNavigationSessionQuery, Result<NavigationSessionResponse>>
{
    private const int MaxAttempts = 3;

    public async Task<Result<NavigationSessionResponse>> Handle(
        GetNavigationSessionQuery request,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await dbContext.ExecuteInSerializableTransactionAsync(
                    transactionToken => ReadAsync(request, transactionToken),
                    cancellationToken);
            }
            catch (Exception exception) when (attempt < MaxAttempts && dbContext.IsDeadlock(exception))
            {
                dbContext.ClearTrackedEntities();
            }
        }
    }

    private async Task<Result<NavigationSessionResponse>> ReadAsync(
        GetNavigationSessionQuery request,
        CancellationToken cancellationToken)
    {
        var session = await dbContext.TripSessions
            .AsNoTracking()
            .Include(candidate => candidate.Itinerary)
            .ThenInclude(itinerary => itinerary.SchedulingRequest)
            .Include(candidate => candidate.Itinerary)
            .ThenInclude(itinerary => itinerary.Items)
            .Include(candidate => candidate.Items)
            .SingleOrDefaultAsync(candidate => candidate.Id == request.SessionId, cancellationToken);
        if (session is null)
        {
            return Result.Failure<NavigationSessionResponse>(
                NavigationErrorCodes.SessionNotFound,
                "The navigation session was not found.");
        }

        if (session.TravelerUserId != request.TravelerUserId)
        {
            return AccessDenied();
        }

        var access = await itineraryAccessService.ResolveCurrentAsync(
            session.RequestedItineraryId,
            request.TravelerUserId,
            trackCurrent: false,
            cancellationToken);
        if (access.IsFailure)
        {
            return AccessDenied();
        }

        // Untracked: shows the effective state without persisting a read-time expiry.
        session.ExpireIfDue(clock.UtcNow);
        return Result.Success(NavigationSessionResponse.From(session, session.Itinerary));
    }

    private static Result<NavigationSessionResponse> AccessDenied() =>
        Result.Failure<NavigationSessionResponse>(
            NavigationErrorCodes.AccessDenied,
            "You do not have access to this navigation session.");
}