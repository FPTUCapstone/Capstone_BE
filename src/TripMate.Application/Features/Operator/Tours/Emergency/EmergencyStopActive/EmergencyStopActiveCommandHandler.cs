using MediatR;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Tours.Emergency.EmergencyStopActive;

public sealed class EmergencyStopActiveCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    IPasswordHasherService passwordHasher,
    IWeatherSafetyReviewNotifier reviewNotifier)
    : IRequestHandler<EmergencyStopActiveCommand, Result<EmergencyTourActionResponse>>
{
    private readonly EmergencyTourCommandHandler executor =
        new(dbContext, currentUser, clock, passwordHasher, reviewNotifier);

    public Task<Result<EmergencyTourActionResponse>> Handle(
        EmergencyStopActiveCommand request,
        CancellationToken cancellationToken) => executor.Handle(request, cancellationToken);
}