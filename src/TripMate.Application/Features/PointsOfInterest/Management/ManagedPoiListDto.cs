using TripMate.Application.Features.PointsOfInterest.Common;

namespace TripMate.Application.Features.PointsOfInterest.Management;

public sealed record ManagedPoiListDto(
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    ManagedPoiMetadataDto Metadata,
    IReadOnlyCollection<ManagedPoiListItemDto> Items);

public sealed record ManagedPoiMetadataDto(IReadOnlyCollection<ManagedPoiCategoryDto> Categories);

public sealed record ManagedPoiCategoryDto(int Id, string Name);

public sealed record ManagedPoiListItemDto(
    string Id,
    string Name,
    int CategoryId,
    string CategoryName,
    string? Address,
    decimal Latitude,
    decimal Longitude,
    IReadOnlyCollection<PoiOpeningHourDto> OpeningHours,
    string Status);