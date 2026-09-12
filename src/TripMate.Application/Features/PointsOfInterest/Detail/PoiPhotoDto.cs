namespace TripMate.Application.Features.PointsOfInterest.Detail;

public sealed record PoiPhotoDto(
    long Id,
    string Url,
    string? Caption,
    int SortOrder);