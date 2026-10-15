using FluentValidation;

namespace TripMate.Application.Features.Admin.Payouts.GetList;

public sealed class GetPayoutsQueryValidator : AbstractValidator<GetPayoutsQuery>
{
    // Status is intentionally NOT validated here. GetPayoutsQueryHandler owns that check and
    // returns Result.Failure(PayoutErrorCodes.InvalidStatus, ...), which HandleFailure maps to a
    // 400 carrying a stable errorCode. The FluentValidation pipeline runs before the handler, so a
    // duplicate rule here would intercept the request first and produce a plain
    // ValidationProblemDetails with no errorCode — exactly the gap this split avoids.
    public GetPayoutsQueryValidator()
    {
        RuleFor(query => query.Keyword)
            .Must(value => value is null || value.Trim().Length <= GetPayoutsQuery.MaximumKeywordLength)
            .WithErrorCode(PayoutErrorCodes.InvalidFilter)
            .WithMessage($"Keyword must not exceed {GetPayoutsQuery.MaximumKeywordLength} characters.");
        RuleFor(query => query.PeriodFrom)
            .Must(value => !value.HasValue || value.Value < DateOnly.MaxValue)
            .WithErrorCode(PayoutErrorCodes.InvalidFilter)
            .WithMessage("PeriodFrom is outside the supported range.");
        RuleFor(query => query.PeriodTo)
            .Must(value => !value.HasValue || value.Value < DateOnly.MaxValue)
            .WithErrorCode(PayoutErrorCodes.InvalidFilter)
            .WithMessage("PeriodTo is outside the supported range.");
        RuleFor(query => query.PageNumber)
            .GreaterThanOrEqualTo(1)
            .WithErrorCode(PayoutErrorCodes.InvalidFilter);
        RuleFor(query => query.PageSize)
            .InclusiveBetween(1, GetPayoutsQuery.MaximumPageSize)
            .WithErrorCode(PayoutErrorCodes.InvalidFilter);
    }
}