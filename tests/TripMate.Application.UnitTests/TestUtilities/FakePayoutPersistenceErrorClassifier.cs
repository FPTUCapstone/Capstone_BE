using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Operator.Payouts.Common;

namespace TripMate.Application.UnitTests.TestUtilities;

public sealed class FakePayoutPersistenceErrorClassifier : IPayoutPersistenceErrorClassifier
{
    public bool ActiveOperatorConflict { get; set; }

    public bool SettlementWorkItemConflict { get; set; }

    public bool IsActiveOperatorConflict(DbUpdateException exception) => ActiveOperatorConflict;

    public bool IsSettlementWorkItemConflict(DbUpdateException exception) => SettlementWorkItemConflict;
}
