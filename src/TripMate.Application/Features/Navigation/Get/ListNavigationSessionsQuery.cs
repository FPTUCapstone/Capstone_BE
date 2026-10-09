using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Navigation.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Navigation.Get;

public sealed record ListNavigationSessionsQuery(long TravelerUserId, string? State)
    : IRequest<Result<IReadOnlyCollection<NavigationSessionResponse>>>
{
    /// <summary>Selects the Traveler's open session: Navigating or Exploring and not yet expired.</summary>
    public const string OpenStateFilter = "Open";
}

public sealed class ListNavigationSessionsQueryHandler(
    IApplicationDbContext dbContext,
    IItineraryAccessService itineraryAccessService,
    IDateTimeProvider clock)
    : IRequestHandler<ListNavigationSessionsQuery, Result<IReadOnlyCollection<NavigationSessionResponse>>>
{
    public async Task<Result<IReadOnlyCollection<NavigationSessionResponse>>> Handle(
        ListNavigationSessionsQuery request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.State, ListNavigationSessionsQuery.OpenStateFilter, StringComparison.Ordinal))
        {
            return Result.Failure<IReadOnlyCollection<NavigationSessionResponse>>(
                NavigationErrorCodes.UnsupportedStateFilter,
                $"Only state={ListNavigationSessionsQuery.OpenStateFilter} is supported.");
        }

        var session = await dbContext.TripSessions
            .AsNoTracking()
            .Include(candidate => candidate.Itinerary)
            .Include(candidate => candidate.Items)
            .SingleOrDefaultAsync(
                candidate => candidate.TravelerUserId == request.TravelerUserId
                    && candidate.EndedAtUtc == null
                    && (candidate.FsmState == TripSession.NavigatingState
                        || candidate.FsmState == TripSession.ExploringState),
                cancellationToken);
        if (session is null || session.ExpireIfDue(clock.UtcNow))
        {
            return Result.Success<IReadOnlyCollection<NavigationSessionResponse>>([]);
        }

        var access = await itineraryAccessService.ResolveCurrentAsync(
            session.RequestedItineraryId,
            request.TravelerUserId,
            trackCurrent: false,
            cancellationToken);
        return access.IsFailure
            ? Result.Failure<IReadOnlyCollection<NavigationSessionResponse>>(
                NavigationErrorCodes.AccessDenied,
                "You do not have access to this navigation session.")
            : Result.Success<IReadOnlyCollection<NavigationSessionResponse>>(
                [NavigationSessionResponse.From(session, session.Itinerary.Version)]);
    }
}