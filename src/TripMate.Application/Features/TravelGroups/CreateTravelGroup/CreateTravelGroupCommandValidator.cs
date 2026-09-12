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
            .NotEmpty()
            .WithMessage("This field is required.") // Locked content: MSG01
            .MaximumLength(TravelGroupConstants.MaxGroupNameLength);

        RuleFor(x => x.ItineraryId)
            .GreaterThan(0);

        RuleFor(x => x.HostUserId)
            .GreaterThan(0);

        RuleFor(x => x.IdempotencyKey)
            .NotEqual(Guid.Empty);
    }
}
