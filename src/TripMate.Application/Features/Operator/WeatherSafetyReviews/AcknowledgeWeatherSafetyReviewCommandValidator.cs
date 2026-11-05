using FluentValidation;

using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Operator.WeatherSafetyReviews;

public sealed class AcknowledgeWeatherSafetyReviewCommandValidator
    : AbstractValidator<AcknowledgeWeatherSafetyReviewCommand>
{
    public AcknowledgeWeatherSafetyReviewCommandValidator()
    {
        RuleFor(command => command.ReviewId).GreaterThan(0).WithMessage("Mã hồ sơ cảnh báo không hợp lệ.");
        RuleFor(command => command.ImmediateInstructions)
            .MaximumLength(WeatherSafetyReview.InstructionsMaxLength)
            .WithMessage("Ghi chú xác nhận nội bộ tối đa 1000 ký tự.");
    }
}
