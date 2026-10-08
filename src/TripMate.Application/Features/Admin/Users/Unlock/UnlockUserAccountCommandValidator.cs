using FluentValidation;

namespace TripMate.Application.Features.Admin.Users.Unlock;

public sealed class UnlockUserAccountCommandValidator : AbstractValidator<UnlockUserAccountCommand>
{
    public UnlockUserAccountCommandValidator()
    {
        RuleFor(command => command.UserId).GreaterThan(0);
        RuleFor(command => command.Reason)
            .Cascade(CascadeMode.Stop)
            .Must(reason => !string.IsNullOrWhiteSpace(reason))
            .MaximumLength(1000);
        RuleFor(command => command.IdempotencyKey)
            .Cascade(CascadeMode.Stop)
            .Must(key => !string.IsNullOrWhiteSpace(key))
            .Must(key => key is not null
                && key.Trim().Length is >= 1 and <= 128
                && key.Trim().All(character => character is >= '!' and <= '~'));
    }
}