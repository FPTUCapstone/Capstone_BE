namespace TripMate.Infrastructure.Weather;

/// <summary>
/// The public weather page uses its own Tomorrow.io key, so its traffic can never use up the quota of the tour weather
/// warnings (<c>TomorrowIo</c>) or the itinerary tips and trip alerts (<c>Tomorrow</c>). No webhook is involved.
/// </summary>
public sealed class PublicWeatherOptions
{
    public const string SectionName = "PublicWeather";

    public bool Enabled { get; init; }
    public string? ApiKey { get; init; }
    public string BaseUrl { get; init; } = "https://api.tomorrow.io/";
    public TimeSpan CacheDuration { get; init; } = TimeSpan.FromMinutes(30);
}