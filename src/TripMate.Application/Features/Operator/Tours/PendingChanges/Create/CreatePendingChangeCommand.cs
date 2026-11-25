using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Tours.PendingChanges.Create;

public sealed record CreatePendingChangeCommand(long TourId)
    : IRequest<Result<PendingChangeResponse>>;

public sealed record PendingChangeResponse(
    long Id,
    long TourId,
    string Status,
    bool IsNew);