using MediatR;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Navigation.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Navigation.Complete;

public sealed class CompleteNavigationSessionCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock)
    : IRequestHandler<CompleteNavigationSessionCommand, Result<NavigationSessionResponse>>
{
    // The owner may always finish, so revoked itinerary access never leaves an unclosable session.
    public Task<Result<NavigationSessionResponse>> Handle(
        CompleteNavigationSessionCommand request,
        CancellationToken cancellationToken) =>
        new NavigationSessionMutator(dbContext, clock).ExecuteAsync(
            request.SessionId,
            request.TravelerUserId,
            requiredItineraryAccess: null,
            (session, now) => session.Finish(now)
                ? TripSessionProgressOutcome.Applied
                : TripSessionProgressOutcome.Replayed,
            NavigationSessionMetrics.RecordCompletion,
            cancellationToken);
}