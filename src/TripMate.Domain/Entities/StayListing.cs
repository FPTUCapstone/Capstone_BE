namespace TripMate.Domain.Entities;

public sealed class StayListing
{
    private StayListing() { }

    public StayListing(long poiId) => PoiId = poiId > 0 ? poiId : throw new ArgumentOutOfRangeException(nameof(poiId));

    public long PoiId { get; private set; }
    public string? Description { get; private set; }
    public bool Published { get; private set; }
    public bool AcceptingRequests { get; private set; }

    public void Update(string? description, bool published, bool acceptingRequests)
    {
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Published = published;
        AcceptingRequests = published && acceptingRequests;
    }
}
