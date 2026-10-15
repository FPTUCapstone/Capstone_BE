using System.Text.Json.Serialization;

namespace TripMate.Application.Features.Tours.Search.V2;

/// <summary>Canonical V2 response shape; not exposed by the current production search.</summary>
public sealed record TourSearchV2ItemDto(
    string TourId,
    string Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ThumbnailUrl,
    IReadOnlyList<string> Destinations,
    int DurationDays,
    long BasePrice,
    string Currency,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? AggregateRating,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? RemainingSlots,
    string AvailabilityStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RepresentativeScheduleId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? DepartureAtUtc,
    string OperatorName);

public sealed record PagedToursV2ResponseDto(
    int Page,
    int PageSize,
    long TotalCount,
    long TotalPages,
    DateTime AsOfUtc,
    IReadOnlyList<TourSearchV2ItemDto> Items);
