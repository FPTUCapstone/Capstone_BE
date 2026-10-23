using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripAlerts.Common;

namespace TripMate.Application.Features.TripAlerts.List;

public sealed record ListTripAlertsQuery(long SessionId, long TravelerUserId)
    : IRequest<Result<IReadOnlyCollection<TripAlertResponse>>>;