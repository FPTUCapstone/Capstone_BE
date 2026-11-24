using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Rerouting.Reject;

/// <summary>Placeholder until plan task T7; the route and contract are tested in T6.</summary>
public sealed class RejectReroutingProposalCommandHandler
    : IRequestHandler<RejectReroutingProposalCommand, Result>
{
    public Task<Result> Handle(RejectReroutingProposalCommand request, CancellationToken cancellationToken) =>
        throw new NotImplementedException("UC-15 reject is implemented in plan task T7.");
}