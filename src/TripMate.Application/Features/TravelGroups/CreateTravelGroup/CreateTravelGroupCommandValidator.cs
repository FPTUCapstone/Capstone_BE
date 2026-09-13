using FluentValidation;

using TripMate.Domain.Constants;

namespace TripMate.Application.Features.TravelGroups.CreateTravelGroup;

/**
 * [UC-17] Create Travel Group Command Validator
 * Enforces business rules and message constraints:
 *   - MSG01: "This field is required." when GroupName is empty.
 *   - GroupName length <= MaxGroupNameLength characters.
 *   - ItineraryId and HostUserId must be valid positive IDs.
 */
public class CreateTravelGroupCommandValidator : AbstractValidator<CreateTravelGroupCommand>
{
    public CreateTravelGroupCommandValidator()
    {
        RuleFor(x => x.GroupName)
            .Cascade(CascadeMode.Stop)
            .Must(name => !string.IsNullOrWhiteSpace(name))
            .WithMessage("This field is required.") // Locked content: MSG01
            .Must(name => name is not null
                && name.Trim().Length <= TravelGroupConstants.MaxGroupNameLength)
            .WithMessage($"Group name must not exceed {TravelGroupConstants.MaxGroupNameLength} characters.");

        RuleFor(x => x.ItineraryId)
            .GreaterThan(0);

        RuleFor(x => x.HostUserId)
            .GreaterThan(0);

        RuleFor(x => x.IdempotencyKey)
            .NotEqual(Guid.Empty);
    }
}