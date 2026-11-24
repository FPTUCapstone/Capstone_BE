using FluentValidation;

using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Navigation.Common;

namespace TripMate.Application.Features.Rerouting.Accept;

/// <summary>Accept Re-routing (SRS 3.3.6 step 6): the only way a proposal ever changes the itinerary (BR-33).</summary>
public sealed record AcceptReroutingProposalCommand(
    long SessionId,
    long ProposalId,
    long TravelerUserId) : IRequest<Result<NavigationSessionResponse>>;

public sealed class AcceptReroutingProposalCommandValidator : AbstractValidator<AcceptReroutingProposalCommand>
{
    public AcceptReroutingProposalCommandValidator()
    {
        RuleFor(command => command.SessionId).GreaterThan(0);
        RuleFor(command => command.ProposalId).GreaterThan(0);
    }
}