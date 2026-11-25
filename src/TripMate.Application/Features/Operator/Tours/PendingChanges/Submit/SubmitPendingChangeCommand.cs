using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Tours.PendingChanges.Submit;

public sealed record SubmitPendingChangeCommand(long TourId, long PendingChangeId)
    : IRequest<Result<PendingChangeSubmittedResponse>>;

public sealed record PendingChangeSubmittedResponse(long Id, long TourId, string Status);