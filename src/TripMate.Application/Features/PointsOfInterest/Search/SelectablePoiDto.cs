namespace TripMate.Application.Features.PointsOfInterest.Search;

public sealed record SelectablePoiDto(
    long Id,
    string Name,
    string CategoryName,
    string? Address,
    decimal Latitude,
    decimal Longitude,
    int AverageVisitDurationMinutes,
    decimal? EstimatedVisitCost,
    bool OpeningHoursKnown,
    bool HasShelter);

public sealed record SelectablePoiSearchResult(
    IReadOnlyCollection<SelectablePoiDto> Items,
    int Page,
    int PageSize,
    int TotalCount);