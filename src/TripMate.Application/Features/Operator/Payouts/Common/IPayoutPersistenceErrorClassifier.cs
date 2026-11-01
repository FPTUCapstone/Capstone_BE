using Microsoft.EntityFrameworkCore;

namespace TripMate.Application.Features.Operator.Payouts.Common;

public interface IPayoutPersistenceErrorClassifier
{
    bool IsActiveOperatorConflict(DbUpdateException exception);

    /// <summary>UC-65: the unique payout_id of the settlement work item was violated, meaning another
    /// authorization already queued this payout.</summary>
    bool IsSettlementWorkItemConflict(DbUpdateException exception);
}