using Microsoft.Extensions.Options;

using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TripAlerts.Weather;

public enum WeatherAlertAction
{
    None,
    TransientAdvisory,
    PersistedAdvisory,
    Warning,
}

public sealed class WeatherAlertPolicy(IOptions<TripAlertOptions> options)
{
    public const string ModerateRainSourcePrefix = "tomorrow:rain-moderate:";
    public const string HeavyRainSourcePrefix = "tomorrow:rain-heavy:";
    public const string ExtremeRainSourcePrefix = "tomorrow:rain-extreme:";
    public const string ThunderstormSourcePrefix = "tomorrow:thunderstorm:";
    private readonly TripAlertOptions options = options.Value;

    public WeatherAlertAction Decide(double rainMmPerHour, bool thunderstorm,
        IndoorOutdoorType indoorOutdoor, bool classificationReviewed,
        TimeSpan untilVisit, TripWeatherBasis basis)
    {
        if (!double.IsFinite(rainMmPerHour) || rainMmPerHour < 0) return WeatherAlertAction.None;
        if (basis == TripWeatherBasis.CurrentAtDestination)
        {
            return IsWarning(rainMmPerHour, thunderstorm, indoorOutdoor, classificationReviewed)
                ? WeatherAlertAction.Warning : WeatherAlertAction.None;
        }

        if (basis != TripWeatherBasis.ForecastAtPlannedTime
            || untilVisit < TimeSpan.Zero || untilVisit > TimeSpan.FromHours(3))
        {
            return WeatherAlertAction.None;
        }

        if (IsWarning(rainMmPerHour, thunderstorm, indoorOutdoor, classificationReviewed))
        {
            return WeatherAlertAction.Warning;
        }

        if (classificationReviewed && indoorOutdoor == IndoorOutdoorType.Outdoor
            && rainMmPerHour >= options.AdvisoryRainMmPerHour
            && rainMmPerHour < options.SevereRainMmPerHour)
        {
            return untilVisit <= TimeSpan.FromHours(1)
                ? WeatherAlertAction.PersistedAdvisory
                : WeatherAlertAction.TransientAdvisory;
        }

        return WeatherAlertAction.None;
    }

    private bool IsWarning(double rain, bool thunderstorm,
        IndoorOutdoorType indoorOutdoor, bool reviewed) =>
        thunderstorm || rain >= options.ExtremeRainMmPerHour
        || (reviewed && indoorOutdoor == IndoorOutdoorType.Outdoor
            && rain >= options.SevereRainMmPerHour);
}