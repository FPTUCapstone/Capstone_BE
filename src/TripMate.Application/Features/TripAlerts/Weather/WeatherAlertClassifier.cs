using Microsoft.Extensions.Options;

using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TripAlerts.Weather;

public enum WeatherAlertLevel
{
    None = 0,
    Advisory = 1,
    Warning = 2,
}

public sealed class WeatherAlertClassifier(IOptions<TripAlertOptions> options)
{
    private readonly TripAlertOptions options = options.Value;

    public WeatherAlertLevel Classify(
        double rainMmPerHour,
        bool thunderstorm,
        bool hasOfficialAlert,
        IndoorOutdoorType indoorOutdoor)
    {
        if (thunderstorm || hasOfficialAlert || rainMmPerHour >= options.SevereRainMmPerHour)
        {
            return WeatherAlertLevel.Warning;
        }

        return rainMmPerHour >= options.AdvisoryRainMmPerHour
               && indoorOutdoor == IndoorOutdoorType.Outdoor
            ? WeatherAlertLevel.Advisory
            : WeatherAlertLevel.None;
    }
}