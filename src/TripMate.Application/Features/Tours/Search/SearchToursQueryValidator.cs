using FluentValidation;

namespace TripMate.Application.Features.Tours.Search;

public sealed class SearchToursQueryValidator : AbstractValidator<SearchToursQuery>
{
    public SearchToursQueryValidator()
    {
        RuleFor(query => query.Destination)
            .Custom((destination, context) =>
            {
                try
                {
                    var normalized = TourSearchCriteria.NormalizeDestination(destination);
                    if (normalized is not null
                        && normalized.Length > TourSearchCriteria.DestinationMaxLength)
                    {
                        context.AddFailure(
                            "Destination",
                            $"Destination must not exceed {TourSearchCriteria.DestinationMaxLength} characters after normalization.");
                    }
                }
                catch (ArgumentException)
                {
                    context.AddFailure("Destination", "Destination contains invalid Unicode text.");
                }
            });

        RuleFor(query => query.DepartureDate)
            .Must(date => date is null || TourSearchCriteria.TryGetDepartureBoundsUtc(
                date.Value,
                out _,
                out _))
            .WithMessage("Departure date cannot be converted to a valid Vietnam calendar day.");

        RuleFor(query => query.MinPrice)
            .InclusiveBetween(0, TourSearchCriteria.MaximumWholeVndPrice)
            .When(query => query.MinPrice.HasValue);
        RuleFor(query => query.MaxPrice)
            .InclusiveBetween(0, TourSearchCriteria.MaximumWholeVndPrice)
            .When(query => query.MaxPrice.HasValue);
        RuleFor(query => query.MaxPrice)
            .GreaterThanOrEqualTo(query => query.MinPrice)
            .When(query => query.MinPrice.HasValue && query.MaxPrice.HasValue)
            .WithMessage("Maximum price must be greater than or equal to minimum price.");

        RuleFor(query => query.Page).GreaterThan(0);
        RuleFor(query => query.PageSize)
            .InclusiveBetween(1, TourSearchCriteria.MaximumPageSize);
        RuleFor(query => query)
            .Must(query => ((long)query.Page - 1) * query.PageSize <= int.MaxValue)
            .When(query => query.Page > 0 && query.PageSize > 0)
            .WithName("Page")
            .WithMessage("Requested page is too large.");
    }
}