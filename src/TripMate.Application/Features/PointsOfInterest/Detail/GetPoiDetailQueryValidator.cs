using FluentValidation;

namespace TripMate.Application.Features.PointsOfInterest.Detail;

public sealed class GetPoiDetailQueryValidator : AbstractValidator<GetPoiDetailQuery>
{
    public GetPoiDetailQueryValidator()
    {
        RuleFor(q => q.Id).GreaterThan(0);
    }
}