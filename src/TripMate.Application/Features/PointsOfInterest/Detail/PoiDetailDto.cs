using System.Text.Json.Serialization;

using TripMate.Application.Common.Serialization;
using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.PointsOfInterest.Detail;

public sealed record PoiDetailDto(
    long Id,
    string Name,
    string? Description,
    [property: JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
    PointOfInterestStatus Status,
    int CategoryId,
    string CategoryName,
    decimal Latitude,
    decimal Longitude,
    string? Address,
    [property: JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
    IndoorOutdoorType IndoorOutdoor,
    int AverageVisitDurationMinutes,
    bool HasShelter,
    decimal? ScenicScore,
    decimal? PhotoRating,
    decimal? AverageRating,
    int ReviewCount,
    bool IsOpenNow,
    IReadOnlyCollection<PoiOpeningHourDto> OpeningHours,
    IReadOnlyCollection<PoiPhotoDto> Photos,
    IReadOnlyCollection<PoiTagDto> Tags,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);