namespace TripMate.Application.Features.Itineraries.WeatherTips;

public sealed class WeatherTipsOptions
{
    public const string SectionName = "WeatherTips";
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 10;

    /// <summary>Off by default. It also needs <c>Tomorrow:Enabled</c> and its API key.</summary>
    public bool Enabled { get; init; }

    /// <summary>Time budget for all forecast calls of one request.</summary>
    public int TimeoutSeconds { get; init; } = 3;
}