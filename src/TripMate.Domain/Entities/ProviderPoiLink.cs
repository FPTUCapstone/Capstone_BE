namespace TripMate.Domain.Entities;

public sealed class ProviderPoiLink
{
    private ProviderPoiLink()
    {
    }

    public long ServiceProviderId { get; private set; }
    public long PointOfInterestId { get; private set; }
    public ServiceProvider Provider { get; private set; } = null!;
    public PointOfInterest Poi { get; private set; } = null!;

    public static ProviderPoiLink Create(ServiceProvider provider, PointOfInterest poi)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(poi);
        return new ProviderPoiLink { Provider = provider, Poi = poi };
    }
}