using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Tours.Emergency;

public sealed record GetEmergencyTourImpactQuery(long TourId, long ScheduleId)
    : IRequest<Result<EmergencyTourImpactResponse>>;

public sealed record EmergencyTourImpactResponse(
    long TourId,
    long ScheduleId,
    string Action,
    string ScheduleStatus,
    int PaidBookingCount,
    int UnpaidBookingCount,
    int TotalPassengerCount,
    int ActiveSessionCount,
    int UnusedTicketCount,
    bool HasUnverifiedSessions,
    string ImpactVersion);
