namespace TripMate.Application.Features.Itineraries.WeatherTips;

/// <summary><see cref="WeatherTips"/> is <c>null</c> whenever the forecast is not available for every visit stop.</summary>
public sealed record ItineraryWeatherTipsResponse(WeatherTipsDto? WeatherTips);

public sealed record WeatherTipsDto(
    string Date,
    string TimeZoneId,
    DateTimeOffset ForecastAtUtc,
    WeatherTipsSummaryDto Summary,
    IReadOnlyList<WeatherTipDto> Tips);

public sealed record WeatherTipsSummaryDto(
    int MinTempC,
    int MaxTempC,
    int MaxRainProbabilityPercent,
    int MaxUvIndex);

public sealed record WeatherTipDto(string Code, string Severity, string Text);