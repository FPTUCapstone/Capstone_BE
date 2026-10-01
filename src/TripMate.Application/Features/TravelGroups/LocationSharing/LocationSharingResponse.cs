using System.Globalization;

namespace TripMate.Application.Features.TravelGroups.LocationSharing;

public sealed record LocationSharingResponse(
    long GroupId,
    bool Enabled,
    DateTimeOffset? UpdatedAtUtc)
{
    public string? SessionVersion => Enabled
        ? UpdatedAtUtc?.UtcTicks.ToString(CultureInfo.InvariantCulture)
        : null;
}