using System.Text.Json.Serialization;

using TripMate.Application.Common.Serialization;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.PointsOfInterest.Explore;

public sealed record PoiListItemDto(
    long Id,
    string Name,
    int CategoryId,
    string CategoryName,
    decimal Latitude,
    decimal Longitude,
    string? Address,
    [property: JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
    IndoorOutdoorType IndoorOutdoor,
    int AverageVisitDurationMinutes,
    bool HasShelter,
    decimal? AverageRating,
    int ReviewCount,
    string? ThumbnailUrl,
    decimal? DistanceKm,
    bool IsOpenNow);