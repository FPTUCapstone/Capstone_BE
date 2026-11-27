using FluentValidation;

namespace TripMate.Application.Features.Weather.PublicForecast;

public sealed class GetPublicWeatherForecastQueryValidator : AbstractValidator<GetPublicWeatherForecastQuery>
{
    public GetPublicWeatherForecastQueryValidator()
    {
        RuleFor(query => query.Latitude)
            .InclusiveBetween(GetPublicWeatherForecastQuery.MinLatitude, GetPublicWeatherForecastQuery.MaxLatitude)
            .WithMessage("Chỉ hỗ trợ địa điểm trong Việt Nam.");
        RuleFor(query => query.Longitude)
            .InclusiveBetween(GetPublicWeatherForecastQuery.MinLongitude, GetPublicWeatherForecastQuery.MaxLongitude)
            .WithMessage("Chỉ hỗ trợ địa điểm trong Việt Nam.");
    }
}