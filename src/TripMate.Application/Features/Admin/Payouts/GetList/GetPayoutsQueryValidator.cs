using FluentValidation;

using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Admin.Payouts.GetList;

public sealed class GetPayoutsQueryValidator : AbstractValidator<GetPayoutsQuery>
{
    public GetPayoutsQueryValidator()
    {
        RuleFor(query => query.Keyword)
            .Must(value => value is null || value.Trim().Length <= GetPayoutsQuery.MaximumKeywordLength)
            .WithMessage($"Keyword must not exceed {GetPayoutsQuery.MaximumKeywordLength} characters.");
        RuleFor(query => query.Status)
            .Must(value => string.IsNullOrWhiteSpace(value) || Payout.AllStatuses.Contains(value))
            .WithMessage("Status is invalid.");
        RuleFor(query => query.PeriodFrom)
            .Must(value => !value.HasValue || value.Value < DateOnly.MaxValue)
            .WithMessage("PeriodFrom is outside the supported range.");
        RuleFor(query => query.PeriodTo)
            .Must(value => !value.HasValue || value.Value < DateOnly.MaxValue)
            .WithMessage("PeriodTo is outside the supported range.");
        RuleFor(query => query.PageNumber).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, GetPayoutsQuery.MaximumPageSize);
    }
}