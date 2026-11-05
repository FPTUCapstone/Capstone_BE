using MediatR;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Tours.Emergency.CancelBeforeDeparture;

public sealed class CancelBeforeDepartureCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    IPasswordHasherService passwordHasher,
    IWeatherSafetyReviewNotifier reviewNotifier)
    : IRequestHandler<CancelBeforeDepartureCommand, Result<EmergencyTourActionResponse>>
{
    private readonly EmergencyTourCommandHandler executor =
        new(dbContext, currentUser, clock, passwordHasher, reviewNotifier);

    public Task<Result<EmergencyTourActionResponse>> Handle(
        CancelBeforeDepartureCommand request,
        CancellationToken cancellationToken) => executor.Handle(request, cancellationToken);
}
