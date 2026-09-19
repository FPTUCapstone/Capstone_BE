using System.Text.RegularExpressions;

using FluentValidation;

namespace TripMate.Application.Features.TravelGroups.JoinTravelGroup;

/**
 * [UC-23] Join Travel Group Command Validator
 * Enforces business rules and message constraints:
 *   - MSG01: "This field is required." when InvitationCode is empty.
 *   - InvitationCode must be exactly 8 alphanumeric characters.
 *   - TravelerUserId must be a valid positive ID.
 *   - IdempotencyKey must not be empty.
 */
public class JoinTravelGroupCommandValidator : AbstractValidator<JoinTravelGroupCommand>
{
    private static readonly Regex InvitationCodeRegex = new("^[A-Za-z0-9]{8}$", RegexOptions.Compiled);

    public JoinTravelGroupCommandValidator()
    {
        RuleFor(x => x.InvitationCode)
            .Cascade(CascadeMode.Stop)
            .Must(code => !string.IsNullOrWhiteSpace(code))
            .WithMessage("This field is required.") // Locked content: MSG01
            .Must(code => code is not null && InvitationCodeRegex.IsMatch(code.Trim()))
            .WithMessage("Invitation code must be 8 alphanumeric characters.");

        RuleFor(x => x.TravelerUserId)
            .GreaterThan(0);

        RuleFor(x => x.IdempotencyKey)
            .NotEqual(Guid.Empty);
    }
}