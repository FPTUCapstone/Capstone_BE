using FluentValidation;

namespace TripMate.Application.Features.Admin.Payouts.GetSettlementList;

public sealed class GetSettlementPayoutsQueryValidator : AbstractValidator<GetSettlementPayoutsQuery>
{
    public GetSettlementPayoutsQueryValidator()
    {
        RuleFor(query => query.Keyword)
            .Must(value => value is null || value.Trim().Length <= GetSettlementPayoutsQuery.MaximumKeywordLength)
            .WithMessage($"Keyword must not exceed {GetSettlementPayoutsQuery.MaximumKeywordLength} characters.");
        RuleFor(query => query.PeriodFrom)
            .Must(value => !value.HasValue || value.Value < DateOnly.MaxValue)
            .WithMessage("PeriodFrom is outside the supported range.");
        RuleFor(query => query.PeriodTo)
            .Must(value => !value.HasValue || value.Value < DateOnly.MaxValue)
            .WithMessage("PeriodTo is outside the supported range.");
        RuleFor(query => query.PageNumber).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, GetSettlementPayoutsQuery.MaximumPageSize);
    }
}
