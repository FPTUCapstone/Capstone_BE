using MediatR;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Navigation.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Navigation.Complete;

public sealed class CompleteNavigationSessionCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock,
    IItineraryAccessService itineraryAccessService)
    : IRequestHandler<CompleteNavigationSessionCommand, Result<NavigationSessionResponse>>
{
    public Task<Result<NavigationSessionResponse>> Handle(
        CompleteNavigationSessionCommand request,
        CancellationToken cancellationToken) =>
        new NavigationSessionMutator(dbContext, clock).ExecuteAsync(
            request.SessionId,
            request.TravelerUserId,
            requiredItineraryAccess: itineraryAccessService,
            (session, now) => session.Finish(now)
                ? TripSessionProgressOutcome.Applied
                : TripSessionProgressOutcome.Replayed,
            NavigationSessionMetrics.RecordCompletion,
            cancellationToken);
}