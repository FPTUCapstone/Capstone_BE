using FluentValidation;

using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Rerouting.Common;

namespace TripMate.Application.Features.Rerouting.Get;

public sealed record GetReroutingProposalQuery(
    long SessionId,
    long ProposalId,
    long TravelerUserId) : IRequest<Result<ReroutingProposalDetailResponse>>;

public sealed class GetReroutingProposalQueryValidator : AbstractValidator<GetReroutingProposalQuery>
{
    public GetReroutingProposalQueryValidator()
    {
        RuleFor(query => query.SessionId).GreaterThan(0);
        RuleFor(query => query.ProposalId).GreaterThan(0);
    }
}