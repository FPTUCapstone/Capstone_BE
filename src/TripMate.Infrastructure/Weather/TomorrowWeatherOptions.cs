namespace TripMate.Infrastructure.Weather;

public sealed class TomorrowWeatherOptions
{
    public const string SectionName = "Tomorrow";

    public bool Enabled { get; init; }
    public string? ApiKey { get; init; }
    public string BaseUrl { get; init; } = "https://api.tomorrow.io/";
    public TimeSpan CacheDuration { get; init; } = TimeSpan.FromMinutes(10);
}