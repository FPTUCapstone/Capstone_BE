using FluentValidation;

namespace TripMate.Application.Features.Operator.Tours.Emergency.GetImpact;

public sealed class GetEmergencyTourImpactQueryValidator
    : AbstractValidator<GetEmergencyTourImpactQuery>
{
    public GetEmergencyTourImpactQueryValidator()
    {
        RuleFor(query => query.TourId).GreaterThan(0);
        RuleFor(query => query.ScheduleId).GreaterThan(0);
    }
}
