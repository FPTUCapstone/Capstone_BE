using FluentValidation;

namespace TripMate.Application.Features.CommercialServices.Detail;

public sealed class GetCommercialServiceDetailQueryValidator
    : AbstractValidator<GetCommercialServiceDetailQuery>
{
    public GetCommercialServiceDetailQueryValidator()
    {
        RuleFor(query => query.Id).GreaterThan(0);
    }
}