using System.Text;

using FluentValidation;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Application.Features.Tours.Search;

public sealed class SearchToursQueryValidator : AbstractValidator<SearchToursQuery>
{
    public SearchToursQueryValidator(IApplicationDbContext? context = null)
    {
        RuleFor(query => query.Keyword)
            .Custom((keyword, validationContext) =>
            {
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    return;
                }

                try
                {
                    if (keyword.Trim().Normalize(NormalizationForm.FormC).Length > 300)
                    {
                        validationContext.AddFailure(
                            "Keyword",
                            "Keyword must not exceed 300 characters after normalization.");
                    }
                }
                catch (ArgumentException)
                {
                    validationContext.AddFailure("Keyword", "Keyword contains invalid Unicode text.");
                }
            });

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
        RuleFor(query => query.StartDate)
            .Must(date => date is null || TourSearchCriteria.TryGetDepartureBoundsUtc(
                date.Value,
                out _,
                out _))
            .WithMessage("Start date cannot be converted to a valid Vietnam calendar day.");
        RuleFor(query => query.EndDate)
            .Must(date => date is null || TourSearchCriteria.TryGetDepartureBoundsUtc(
                date.Value,
                out _,
                out _))
            .WithMessage("End date cannot be converted to a valid Vietnam calendar day.");

        RuleFor(query => query.MinPrice)
            .InclusiveBetween(0, TourSearchCriteria.MaximumWholeVndPrice)
            .When(query => query.MinPrice.HasValue);
        RuleFor(query => query.MaxPrice)
            .InclusiveBetween(0, TourSearchCriteria.MaximumWholeVndPrice)
            .When(query => query.MaxPrice.HasValue);
        RuleFor(query => query.MaxPrice)
            .GreaterThanOrEqualTo(query => query.MinPrice)
            .When(query => query.MinPrice.HasValue && query.MaxPrice.HasValue)
            .WithMessage("Maximum price must be greater than or equal to minimum price.")
            .WithErrorCode(SearchToursQuery.InvalidPriceRangeCode);

        RuleFor(query => query.EndDate)
            .GreaterThanOrEqualTo(query => query.StartDate)
            .When(query => query.StartDate.HasValue && query.EndDate.HasValue)
            .WithMessage("End date must not be earlier than start date.")
            .WithErrorCode(SearchToursQuery.InvalidDateRangeCode);
        RuleFor(query => query)
            .Must(query => !query.DepartureDate.HasValue
                || (!query.StartDate.HasValue && !query.EndDate.HasValue))
            .WithName(query => nameof(query.DepartureDate))
            .WithMessage("Legacy departureDate cannot be combined with startDate or endDate.");
        RuleFor(query => query.DurationDays)
            .GreaterThanOrEqualTo(1)
            .When(query => query.DurationDays.HasValue);
        RuleFor(query => query.MinimumRating)
            .InclusiveBetween(1, 5)
            .When(query => query.MinimumRating.HasValue);
        RuleFor(query => query.Sort)
            .Must(sort => sort is null || SearchToursQuery.AllowedSorts.Contains(sort, StringComparer.Ordinal));

        if (context is not null)
        {
            RuleFor(query => query.Category)
                .MustAsync((code, cancellationToken) => context.TourCategories
                    .AsNoTracking()
                    .AnyAsync(category => category.IsActive && category.Code == code, cancellationToken))
                .When(query => query.Category is not null)
                .WithMessage("Category must be an active server-owned category code.");
        }

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