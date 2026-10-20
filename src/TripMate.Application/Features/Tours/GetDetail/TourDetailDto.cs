namespace TripMate.Application.Features.Tours.GetDetail;

/// <summary>
/// Public tour detail (UC-26). Identity values are strings so 64-bit ids survive JSON clients;
/// prices are whole VND. Never serialize entities: every field here is an explicit allow-list entry.
/// </summary>
public sealed record TourDetailDto(
    string TourId,
    string Title,
    string? Description,
    string Destination,
    IReadOnlyList<string> Destinations,
    int DurationDays,
    long BasePrice,
    string Currency,
    TourDetailOperatorDto Operator,
    IReadOnlyList<TourDetailImageDto> Images,
    string? Inclusions,
    string? Exclusions,
    TourDetailCancellationPolicyDto CancellationPolicy,
    IReadOnlyList<TourDetailItineraryDayDto> Itinerary,
    IReadOnlyList<TourDetailScheduleDto> Schedules,
    bool HasBookableSchedule,
    TourDetailRatingDto Rating,
    DateTime AsOfUtc);

/// <summary>Only the company name and contact phone are public; nothing else about the operator.</summary>
public sealed record TourDetailOperatorDto(string Name, string? ContactPhone);

public sealed record TourDetailImageDto(string Url, string AltText, string? Caption, bool IsPrimary);

public sealed record TourDetailCancellationRuleDto(int DaysBeforeDeparture, decimal CancellationFeePercent);

public sealed record TourDetailCancellationPolicyDto(IReadOnlyList<TourDetailCancellationRuleDto> Rules);

public sealed record TourDetailPoiDto(
    string PoiId,
    string Name,
    string? Address,
    decimal Latitude,
    decimal Longitude);

public sealed record TourDetailItineraryItemDto(
    int SequenceNo,
    TourDetailPoiDto Poi,
    string? PlannedTime,
    int StayDurationMinutes,
    string? Notes);

public sealed record TourDetailItineraryDayDto(int DayNo, IReadOnlyList<TourDetailItineraryItemDto> Items);

/// <summary>
/// One future departure. <c>RemainingSlots</c> and <c>AvailabilityStatus</c> are a snapshot at
/// <c>AsOfUtc</c>, not a reservation (BR-59).
/// </summary>
public sealed record TourDetailScheduleDto(
    string ScheduleId,
    DateTime StartAtUtc,
    DateTime EndAtUtc,
    string? MeetingPoint,
    DateTime? RegistrationDeadlineAtUtc,
    long AdultPrice,
    long? ChildPrice,
    long? InfantPrice,
    int? RemainingSlots,
    string AvailabilityStatus,
    bool Bookable);

public sealed record TourDetailRatingDto(decimal? Average, int Count);