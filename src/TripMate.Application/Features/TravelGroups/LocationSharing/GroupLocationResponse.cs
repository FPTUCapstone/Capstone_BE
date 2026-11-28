namespace TripMate.Application.Features.TravelGroups.LocationSharing;

public sealed record GroupLocationResponse(
    long UserId,
    decimal Latitude,
    decimal Longitude,
    DateTimeOffset RecordedAtUtc);

public sealed record GroupLocationsResponse(long GroupId, IReadOnlyList<GroupLocationResponse> Locations);