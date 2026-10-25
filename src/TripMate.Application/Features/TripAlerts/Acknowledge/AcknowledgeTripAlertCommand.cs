using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TripAlerts.Acknowledge;

public sealed record AcknowledgeTripAlertCommand(
    long SessionId,
    long IncidentId,
    long TravelerUserId) : IRequest<Result>;