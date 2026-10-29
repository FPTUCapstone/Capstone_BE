namespace TripMate.Application.Features.Operator.CheckIn.Common;

/// <summary>
/// Dynamic participant aggregation progress for a tour departure (D1).
/// </summary>
public sealed record DepartureProgressDto(
    long ScheduleId,
    string TourTitle,
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    string? MeetingPoint,
    int CheckedInParticipants,
    int TotalEligibleParticipants,
    int WaitingParticipants);

/// <summary>
/// Result of a successful check-in operation.
/// </summary>
public sealed record CheckInResultDto(
    string TicketCode,
    string BookingCode,
    string PassengerName,
    int ParticipantCount,
    DateTimeOffset CheckedInAtUtc,
    DepartureProgressDto Departure);

/// <summary>
/// Overview of a departure schedule for operator selection on Screen #88.
/// </summary>
public sealed record OperatorDepartureSummaryDto(
    long ScheduleId,
    long TourId,
    string TourTitle,
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    string? MeetingPoint,
    string Status,
    int CheckedInParticipants,
    int TotalEligibleParticipants,
    int WaitingParticipants);
