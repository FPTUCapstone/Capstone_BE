using FluentValidation;

namespace TripMate.Application.Features.Admin.Payouts.GetDetails;

public sealed class GetPayoutDetailsQueryValidator : AbstractValidator<GetPayoutDetailsQuery>
{
    public GetPayoutDetailsQueryValidator()
    {
        RuleFor(query => query.PayoutId).GreaterThan(0)
            .WithMessage("PayoutId must be a positive integer.");
    }
}