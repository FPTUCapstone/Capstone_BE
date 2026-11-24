namespace TripMate.Application.Features.Itineraries.WeatherTips;

/// <summary>
/// Fixed rules that turn the forecast hours of a day into a summary and a short list of things to bring.
/// Nothing here guesses: it only reads the hours it is given.
/// </summary>
public static class WeatherTipRules
{
    public const int MaxTips = 5;
    public const string WarningSeverity = "Warning";
    public const string InfoSeverity = "Info";

    public const double StormRainMmPerHour = 4;
    public const double RainProbabilityPercent = 40;
    public const double RainMmPerHour = 0.2;
    public const double HeatApparentC = 33;
    public const double UvIndex = 6;
    public const double CoolTemperatureC = 20;
    public const double WindSpeedMps = 10;

    public static (WeatherTipsSummaryDto Summary, IReadOnlyList<WeatherTipDto> Tips) Evaluate(
        IReadOnlyCollection<HourlyForecast> hours)
    {
        ArgumentNullException.ThrowIfNull(hours);
        if (hours.Count == 0)
        {
            throw new ArgumentException("At least one forecast hour is required.", nameof(hours));
        }

        var summary = new WeatherTipsSummaryDto(
            Round(hours.Min(hour => hour.TemperatureC)),
            Round(hours.Max(hour => hour.TemperatureC)),
            Round(hours.Max(hour => hour.RainProbabilityPercent)),
            Round(hours.Max(hour => hour.UvIndex)));

        var tips = new List<WeatherTipDto>();
        if (hours.Any(hour => hour.Thunderstorm || hour.RainMmPerHour >= StormRainMmPerHour))
        {
            tips.Add(new WeatherTipDto(
                "weather_tip.storm", WarningSeverity, "Có dông hoặc mưa to, cân nhắc đổi sang điểm trong nhà"));
        }

        if (hours.Any(hour => hour.RainProbabilityPercent >= RainProbabilityPercent || hour.RainMmPerHour >= RainMmPerHour))
        {
            tips.Add(new WeatherTipDto("weather_tip.rain", InfoSeverity, "Mang ô hoặc áo mưa"));
        }

        if (hours.Any(hour => hour.ApparentTemperatureC >= HeatApparentC))
        {
            tips.Add(new WeatherTipDto("weather_tip.heat", InfoSeverity, "Mang đủ nước uống và mũ nón"));
        }

        if (hours.Any(hour => hour.UvIndex >= UvIndex))
        {
            tips.Add(new WeatherTipDto("weather_tip.uv", InfoSeverity, "Mang kem chống nắng và kính râm"));
        }

        if (hours.Any(hour => hour.TemperatureC <= CoolTemperatureC))
        {
            tips.Add(new WeatherTipDto("weather_tip.cool", InfoSeverity, "Mang áo khoác mỏng"));
        }

        if (hours.Any(hour => hour.WindSpeedMps >= WindSpeedMps))
        {
            tips.Add(new WeatherTipDto(
                "weather_tip.wind", InfoSeverity, "Gió khá mạnh, chú ý khi ra biển hoặc điểm trên cao"));
        }

        // The table order above already puts the only Warning first; the sort keeps that true if a rule is added.
        var ordered = tips
            .Select((tip, index) => (tip, index))
            .OrderBy(entry => entry.tip.Severity == WarningSeverity ? 0 : 1)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.tip)
            .Take(MaxTips)
            .ToArray();
        return (summary, ordered);
    }

    /// <summary>
    /// Whole UTC hours a stop touches. A stop that ends exactly on the hour does not touch the next hour, and a
    /// zero-length stop touches its own hour.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> HourStartsCovering(DateTimeOffset arrivalUtc, DateTimeOffset departureUtc)
    {
        var arrival = arrivalUtc.ToUniversalTime();
        var end = departureUtc > arrivalUtc ? departureUtc.ToUniversalTime() : arrival;
        var first = new DateTimeOffset(arrival.Year, arrival.Month, arrival.Day, arrival.Hour, 0, 0, TimeSpan.Zero);

        var hours = new List<DateTimeOffset>();
        for (var hour = first; hour < end || hour == first; hour = hour.AddHours(1))
        {
            hours.Add(hour);
        }

        return hours;
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}