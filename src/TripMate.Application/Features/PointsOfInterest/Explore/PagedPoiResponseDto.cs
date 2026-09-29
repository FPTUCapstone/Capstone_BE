namespace TripMate.Application.Features.PointsOfInterest.Explore;

public sealed record PagedPoiResponseDto(
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    IReadOnlyCollection<PoiListItemDto> Items);