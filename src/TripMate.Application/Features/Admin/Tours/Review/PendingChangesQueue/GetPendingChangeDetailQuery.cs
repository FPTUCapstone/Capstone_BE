using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Get;

namespace TripMate.Application.Features.Admin.Tours.Review.PendingChangesQueue;

/// <summary>
/// Staff's detail view of a PendingReview draft version before deciding (spec gap found during
/// acceptance review of PR #212: the moderation queue only carried a title, and the admin tour
/// detail endpoint reads the live tour, not the proposed values). Reuses
/// GetCurrentPendingChangeResponse's shape for consistency with the operator's own view of the
/// same row.
/// </summary>
public sealed record GetPendingChangeDetailQuery(long PendingChangeId)
    : IRequest<Result<GetCurrentPendingChangeResponse>>;