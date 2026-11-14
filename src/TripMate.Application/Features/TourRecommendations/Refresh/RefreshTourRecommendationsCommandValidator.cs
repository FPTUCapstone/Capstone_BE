using FluentValidation;

using TripMate.Application.Features.Tours.Search;

namespace TripMate.Application.Features.TourRecommendations.Refresh;

public sealed class RefreshTourRecommendationsCommandValidator
    : AbstractValidator<RefreshTourRecommendationsCommand>
{
    public RefreshTourRecommendationsCommandValidator()
    {
        RuleFor(command => command.Destination)
            .Must(destination => string.IsNullOrWhiteSpace(destination)
                || destination.Trim().Length <= TourSearchCriteria.DestinationMaxLength)
            .WithMessage($"Destination must not exceed {TourSearchCriteria.DestinationMaxLength} characters after normalization.");

        RuleFor(command => command.EndDate)
            .GreaterThanOrEqualTo(command => command.StartDate)
            .When(command => command.StartDate.HasValue && command.EndDate.HasValue);

        RuleFor(command => command.MinBudget)
            .Must(IsValidWholeVnd)
            .When(command => command.MinBudget.HasValue);
        RuleFor(command => command.MaxBudget)
            .Must(IsValidWholeVnd)
            .When(command => command.MaxBudget.HasValue);
        RuleFor(command => command.MaxBudget)
            .GreaterThanOrEqualTo(command => command.MinBudget)
            .When(command => command.MinBudget.HasValue && command.MaxBudget.HasValue);
    }

    private static bool IsValidWholeVnd(decimal? value) =>
        value is >= 0 and <= TourSearchCriteria.MaximumWholeVndPrice
        && value == decimal.Truncate(value.Value);
}
