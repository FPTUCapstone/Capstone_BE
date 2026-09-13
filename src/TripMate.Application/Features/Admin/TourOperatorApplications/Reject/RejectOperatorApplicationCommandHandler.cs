using MediatR;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.TourOperatorApplications.Common;

namespace TripMate.Application.Features.Admin.TourOperatorApplications.Reject;

/// <summary>
/// UI dependency stub for UC-50. Full rejection logic is owned by UC-51.
/// </summary>
public class RejectOperatorApplicationCommandHandler : IRequestHandler<RejectOperatorApplicationCommand, Result>
{
    public Task<Result> Handle(RejectOperatorApplicationCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult(Result.Failure(
            TourOperatorApplicationErrorCodes.NotImplemented,
            "Rejection logic is owned by UC-51 and is not yet implemented. MSG116 will be issued by UC-51."));
    }
}
