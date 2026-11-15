using System.Text.Json.Serialization;

namespace TripMate.Application.Features.Tours.Search;

public sealed record TourSearchItemDto(
    string TourId,
    string Title,
    IReadOnlyList<string> Destinations,
    string OperatorName,
    int DurationDays,
    long BasePrice,
    string Currency,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RepresentativeScheduleId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? DepartureAtUtc,
    string AvailabilityStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? RemainingSlots,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ThumbnailUrl,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? AggregateRating);