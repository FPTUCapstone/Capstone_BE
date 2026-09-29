using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>TripMate-managed region used to find tours, not a POI or a tour stop.</summary>
public class Destination : BaseEntity
{
    public const int NameMaxLength = 300;

    private Destination()
    {
    }

    public string Name { get; private set; } = string.Empty;
}