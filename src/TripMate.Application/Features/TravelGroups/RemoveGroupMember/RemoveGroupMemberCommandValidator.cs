using FluentValidation;

namespace TripMate.Application.Features.TravelGroups.RemoveGroupMember;

public sealed class RemoveGroupMemberCommandValidator : AbstractValidator<RemoveGroupMemberCommand>
{
    public RemoveGroupMemberCommandValidator()
    {
        RuleFor(command => command.GroupId).GreaterThan(0);
        RuleFor(command => command.MemberUserId).GreaterThan(0);
        RuleFor(command => command.HostUserId).GreaterThan(0);
        RuleFor(command => command.IdempotencyKey).NotEqual(Guid.Empty);
        RuleFor(command => command).Must(command => command.MemberUserId != command.HostUserId)
            .WithMessage("A Host cannot remove themselves.");
    }
}