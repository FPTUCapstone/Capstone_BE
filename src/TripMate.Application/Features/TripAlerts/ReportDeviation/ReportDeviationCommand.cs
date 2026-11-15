using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripAlerts.Common;

namespace TripMate.Application.Features.TripAlerts.ReportDeviation;

public sealed record ReportDeviationCommand(
    long SessionId,
    long TravelerUserId,
    Guid EventId,
    DateTimeOffset ObservedAtUtc,
    long TargetItemId) : IRequest<Result<TripAlertResponse>>;