using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Tours.PendingChanges.Approve;

public sealed record ApprovePendingChangeCommand(long PendingChangeId)
    : IRequest<Result<PendingChangeDecisionResponse>>;

public sealed record PendingChangeDecisionResponse(long Id, long TourId, string Status, string TourStatus);
