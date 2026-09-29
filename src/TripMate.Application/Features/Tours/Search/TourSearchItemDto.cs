namespace TripMate.Application.Features.Tours.Search;

public sealed record TourSearchItemDto(
    string TourId,
    string Title,
    IReadOnlyList<string> Destinations,
    string OperatorName,
    int DurationDays,
    long BasePrice,
    string Currency,
    string? RepresentativeScheduleId,
    DateTime? DepartureAtUtc,
    string AvailabilityStatus,
    int? RemainingSlots);