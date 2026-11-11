using FluentValidation;

namespace TripMate.Application.Features.Operator.WeatherSafetyReviews;

public sealed class GetWeatherSafetyReviewsQueryValidator : AbstractValidator<GetWeatherSafetyReviewsQuery>
{
    public GetWeatherSafetyReviewsQueryValidator()
    {
        // No filter lists every review; an unknown status is a client error, not an empty list.
        RuleFor(query => query.Status)
            .Must(status => GetWeatherSafetyReviewsQuery.AllowedStatuses.Contains(status, StringComparer.Ordinal))
            .When(query => !string.IsNullOrWhiteSpace(query.Status))
            .WithMessage("Trạng thái lọc không hợp lệ.");
    }
}