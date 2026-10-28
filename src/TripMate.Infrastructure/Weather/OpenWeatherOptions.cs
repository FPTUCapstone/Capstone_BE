namespace TripMate.Infrastructure.Weather;

public sealed class OpenWeatherOptions
{
    public const string SectionName = "OpenWeather";

    public bool Enabled { get; init; }
    public bool EnableOfficialAlerts { get; init; }
    public string BaseUrl { get; init; } = "https://api.openweathermap.org/";
    public string? ApiKey { get; init; }
    public TimeSpan CacheDuration { get; init; } = TimeSpan.FromMinutes(10);
}