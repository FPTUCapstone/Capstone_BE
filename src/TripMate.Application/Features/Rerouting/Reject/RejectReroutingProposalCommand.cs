using FluentValidation;

using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Rerouting.Reject;

/// <summary>Keep Current Route (SRS 3.3.6 step 11): the itinerary and the session are not touched.</summary>
public sealed record RejectReroutingProposalCommand(
    long SessionId,
    long ProposalId,
    long TravelerUserId) : IRequest<Result>;

public sealed class RejectReroutingProposalCommandValidator : AbstractValidator<RejectReroutingProposalCommand>
{
    public RejectReroutingProposalCommandValidator()
    {
        RuleFor(command => command.SessionId).GreaterThan(0);
        RuleFor(command => command.ProposalId).GreaterThan(0);
    }
}