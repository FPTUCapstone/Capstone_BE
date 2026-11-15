namespace TripMate.Infrastructure.Routing;

public sealed class MapboxRoutingOptions
{
    public const string SectionName = "MapboxRouting";

    public string BaseUrl { get; init; } = "https://api.mapbox.com/";

    public string AccessToken { get; init; } = string.Empty;

    /// <summary>
    /// Mapbox has no motorbike profile; motorbike legs use the driving road network and its duration
    /// multiplied by this factor (city traffic lets motorbikes move a bit faster). 1.0 = car time.
    /// </summary>
    public double MotorbikeDurationFactor { get; init; } = DefaultMotorbikeDurationFactor;

    public const double DefaultMotorbikeDurationFactor = 0.9;

    public const double MinMotorbikeDurationFactor = 0.5;

    public const double MaxMotorbikeDurationFactor = 1.5;
}