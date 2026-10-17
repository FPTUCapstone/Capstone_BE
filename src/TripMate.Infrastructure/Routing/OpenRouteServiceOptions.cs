namespace TripMate.Infrastructure.Routing;

public sealed class OpenRouteServiceOptions
{
    public const string SectionName = "OpenRouteService";

    public string BaseUrl { get; init; } = "https://api.heigit.org/openrouteservice/";

    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// OpenRouteService has no motorbike profile; motorbike legs use the car road network and its
    /// duration multiplied by this factor (city traffic lets motorbikes move a bit faster). 1.0 = car time.
    /// </summary>
    public double MotorbikeDurationFactor { get; init; } = DefaultMotorbikeDurationFactor;

    public const double DefaultMotorbikeDurationFactor = 0.9;

    public const double MinMotorbikeDurationFactor = 0.5;

    public const double MaxMotorbikeDurationFactor = 1.5;
}