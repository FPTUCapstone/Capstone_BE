using FluentValidation;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Application.Features.Tours.Search.V2;

public sealed class TourSearchV2RequestValidator : AbstractValidator<TourSearchV2Request>
{
    public TourSearchV2RequestValidator(IApplicationDbContext context)
    {
        RuleFor(request => request.EndDate).GreaterThanOrEqualTo(request => request.StartDate)
            .When(request => request.StartDate.HasValue && request.EndDate.HasValue)
            .WithMessage(TourSearchV2Request.InvalidDateRangeMessage)
            .WithErrorCode(TourSearchV2Request.InvalidDateRangeCode);
        RuleFor(request => request.MinPrice).InclusiveBetween(0, TourSearchCriteria.MaximumWholeVndPrice)
            .When(request => request.MinPrice.HasValue);
        RuleFor(request => request.MaxPrice).InclusiveBetween(0, TourSearchCriteria.MaximumWholeVndPrice)
            .When(request => request.MaxPrice.HasValue);
        RuleFor(request => request.MaxPrice).GreaterThanOrEqualTo(request => request.MinPrice)
            .When(request => request.MinPrice.HasValue && request.MaxPrice.HasValue)
            .WithMessage(TourSearchV2Request.InvalidPriceRangeMessage)
            .WithErrorCode(TourSearchV2Request.InvalidPriceRangeCode);
        RuleFor(request => request.DurationDays).GreaterThanOrEqualTo(1).When(request => request.DurationDays.HasValue);
        RuleFor(request => request.MinimumRating).InclusiveBetween(1, 5).When(request => request.MinimumRating.HasValue);
        RuleFor(request => request.Sort).Must(sort => TourSearchV2Request.AllowedSorts.Contains(sort, StringComparer.Ordinal));
        RuleFor(request => request.Page).GreaterThanOrEqualTo(1);
        RuleFor(request => request.PageSize).InclusiveBetween(1, TourSearchCriteria.MaximumPageSize);
        RuleFor(request => request.Category)
            .MustAsync((code, cancellationToken) => context.TourCategories.AsNoTracking()
                .AnyAsync(category => category.IsActive && category.Code == code, cancellationToken))
            .When(request => request.Category is not null)
            .WithMessage("Category must be an active server-owned category code.");
    }
}