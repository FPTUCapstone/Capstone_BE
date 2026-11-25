using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Tours.PendingChanges.Discard;

public sealed record DiscardPendingChangeCommand(long TourId, long PendingChangeId)
    : IRequest<Result>;
