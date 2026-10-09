using FluentValidation;

namespace TripMate.Application.Features.Admin.ActiveTrips.GetDetails;

public sealed class GetActiveTripDetailsQueryValidator : AbstractValidator<GetActiveTripDetailsQuery>
{
    public GetActiveTripDetailsQueryValidator()
    {
        RuleFor(query => query.TripId).GreaterThan(0)
            .WithMessage("TripId must be a positive integer.");
    }
}