using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Approve;

namespace TripMate.Application.Features.Operator.Tours.PendingChanges.Reject;

public sealed record RejectPendingChangeCommand(long PendingChangeId, string Reason)
    : IRequest<Result<PendingChangeDecisionResponse>>;