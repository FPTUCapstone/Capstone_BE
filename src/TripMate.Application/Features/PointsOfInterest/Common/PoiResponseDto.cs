using System.Text.Json.Serialization;

using TripMate.Application.Common.Serialization;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.PointsOfInterest.Common;

public sealed record PoiResponseDto(
    long Id,
    int CategoryId,
    string Name,
    string? Description,
    decimal Latitude,
    decimal Longitude,
    string? Address,
    [property: JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
    IndoorOutdoorType IndoorOutdoor,
    decimal? ScenicScore,
    decimal? PhotoRating,
    int AverageVisitDurationMinutes,
    bool HasShelter,
    [property: JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
    PointOfInterestStatus Status,
    long CreatedById,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyCollection<PoiOpeningHourDto> OpeningHours,
    IReadOnlyCollection<int> TagIds);

public sealed record PoiOpeningHourDto(
    int DayOfWeek,
    TimeOnly? OpenTime,
    TimeOnly? CloseTime,
    bool IsClosed);