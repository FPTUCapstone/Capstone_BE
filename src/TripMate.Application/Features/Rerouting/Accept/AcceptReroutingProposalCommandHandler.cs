using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Navigation.Common;

namespace TripMate.Application.Features.Rerouting.Accept;

/// <summary>Placeholder until plan task T7 (the transactional accept pipeline); the route and contract are tested in T6.</summary>
public sealed class AcceptReroutingProposalCommandHandler
    : IRequestHandler<AcceptReroutingProposalCommand, Result<NavigationSessionResponse>>
{
    public Task<Result<NavigationSessionResponse>> Handle(
        AcceptReroutingProposalCommand request,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException("UC-15 accept is implemented in plan task T7.");
}