using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Operator.Payouts.Common;

namespace TripMate.Application.UnitTests.TestUtilities;

public sealed class FakePayoutPersistenceErrorClassifier : IPayoutPersistenceErrorClassifier
{
    public bool IsActiveOperatorConflict(DbUpdateException exception) => false;
}