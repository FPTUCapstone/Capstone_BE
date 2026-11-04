using FluentValidation;

using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Operator.WeatherSafetyReviews;

public sealed class AcknowledgeWeatherSafetyReviewCommandValidator
    : AbstractValidator<AcknowledgeWeatherSafetyReviewCommand>
{
    public AcknowledgeWeatherSafetyReviewCommandValidator()
    {
        RuleFor(command => command.ReviewId).GreaterThan(0);
        RuleFor(command => command.ImmediateInstructions)
            .MaximumLength(WeatherSafetyReview.InstructionsMaxLength);
    }
}
