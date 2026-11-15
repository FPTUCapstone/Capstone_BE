namespace TripMate.Application.Features.Operator.Tours.Emergency;

public sealed record EmergencyTourActionResponse(
    long OperationId,
    long TourScheduleId,
    string Action,
    string ScheduleStatus,
    int AffectedBookingCount,
    DateTimeOffset CommittedAtUtc,
    int RefundsSubmitted);