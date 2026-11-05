using FluentValidation;

namespace TripMate.Application.Features.Operator.Tours.Emergency.GetImpact;

public sealed class GetEmergencyTourImpactQueryValidator
    : AbstractValidator<GetEmergencyTourImpactQuery>
{
    public GetEmergencyTourImpactQueryValidator()
    {
        RuleFor(query => query.TourId).GreaterThan(0).WithMessage("Mã tour không hợp lệ.");
        RuleFor(query => query.ScheduleId).GreaterThan(0).WithMessage("Mã lịch khởi hành không hợp lệ.");
    }
}
