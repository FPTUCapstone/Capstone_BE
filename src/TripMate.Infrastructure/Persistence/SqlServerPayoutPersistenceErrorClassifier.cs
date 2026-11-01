using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Operator.Payouts.Common;

namespace TripMate.Infrastructure.Persistence;

public sealed class SqlServerPayoutPersistenceErrorClassifier : IPayoutPersistenceErrorClassifier
{
    private const string ActiveOperatorIndex = "UX_Payouts_ActiveOperator";
    private const string SettlementWorkItemConstraint = "UX_PayoutSettlementWorkItems_Payout";

    public bool IsActiveOperatorConflict(DbUpdateException exception) =>
        IsUniqueViolationOf(exception, ActiveOperatorIndex);

    public bool IsSettlementWorkItemConflict(DbUpdateException exception) =>
        IsUniqueViolationOf(exception, SettlementWorkItemConstraint);

    private static bool IsUniqueViolationOf(DbUpdateException exception, string name)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException { Number: 2601 or 2627 } sqlException
                && sqlException.Message.Contains(name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}