using MediatR;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Navigation.Common;

namespace TripMate.Application.Features.Navigation.Depart;

public sealed class DepartNavigationSessionCommandHandler(
    IApplicationDbContext dbContext,
    IItineraryAccessService itineraryAccessService,
    IDateTimeProvider clock,
    NavigationSessionOptions? options = null)
    : IRequestHandler<DepartNavigationSessionCommand, Result<NavigationSessionResponse>>
{
    private readonly NavigationSessionOptions _options = options ?? new NavigationSessionOptions();

    public Task<Result<NavigationSessionResponse>> Handle(
        DepartNavigationSessionCommand request,
        CancellationToken cancellationToken) =>
        new NavigationSessionMutator(dbContext, clock).ExecuteAsync(
            request.SessionId,
            request.TravelerUserId,
            itineraryAccessService,
            (session, now) => session.Depart(
                session.ResolveEventTime(request.OccurredAtUtc, now, _options.ClientClockSkewTolerance)),
            NavigationSessionMetrics.RecordDeparture,
            cancellationToken);
}