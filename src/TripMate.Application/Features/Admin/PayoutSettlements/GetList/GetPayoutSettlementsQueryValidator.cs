using FluentValidation;

using TripMate.Application.Features.Admin.Payouts.Common;
using TripMate.Application.Features.Admin.Payouts.GetList;

namespace TripMate.Application.Features.Admin.PayoutSettlements.GetList;

// Status is checked by the handler, not here, so an out-of-scope status reaches the client as a
// 400 carrying the stable payout.invalid_status code (same split as the Staff list validator).
public sealed class GetPayoutSettlementsQueryValidator : AbstractValidator<GetPayoutSettlementsQuery>
{
    public GetPayoutSettlementsQueryValidator()
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