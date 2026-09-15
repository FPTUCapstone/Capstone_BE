namespace TripMate.Infrastructure.Routing;

public sealed class OpenRouteServiceOptions
{
    public const string SectionName = "OpenRouteService";

    public string BaseUrl { get; init; } = "https://api.openrouteservice.org/";

    public string ApiKey { get; init; } = string.Empty;
}
