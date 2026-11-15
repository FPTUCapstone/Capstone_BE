using Microsoft.EntityFrameworkCore;

namespace TripMate.Application.Features.Operator.Payouts.Common;

public interface IPayoutPersistenceErrorClassifier
{
    bool IsActiveOperatorConflict(DbUpdateException exception);
}