using MediatR;

using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Navigation.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Navigation.Reach;

public sealed class ReachNavigationItemCommandHandler(
    IApplicationDbContext dbContext,
    IItineraryAccessService itineraryAccessService,
    IDateTimeProvider clock,
    NavigationSessionOptions? options = null,
    ILogger<ReachNavigationItemCommandHandler>? logger = null)
    : IRequestHandler<ReachNavigationItemCommand, Result<NavigationSessionResponse>>
{
    private readonly NavigationSessionOptions _options = options ?? new NavigationSessionOptions();

    public Task<Result<NavigationSessionResponse>> Handle(
        ReachNavigationItemCommand request,
        CancellationToken cancellationToken) =>
        new NavigationSessionMutator(dbContext, clock, logger).ExecuteAsync(
            request.SessionId,
            request.TravelerUserId,
            itineraryAccessService,
            (session, now) => session.ReachItem(
                request.ItemId,
                session.ResolveEventTime(request.OccurredAtUtc, now, _options.ClientClockSkewTolerance)),
            NavigationSessionMetrics.RecordReach,
            cancellationToken,
            new NavigationBehaviorSignal(request.ItemId, RecommendationEventType.Like));
}