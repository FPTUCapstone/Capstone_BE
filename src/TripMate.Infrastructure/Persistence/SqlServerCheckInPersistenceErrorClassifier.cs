using Microsoft.Data.SqlClient;

using TripMate.Application.Features.Operator.CheckIn.Common;

namespace TripMate.Infrastructure.Persistence;

public sealed class SqlServerCheckInPersistenceErrorClassifier : ICheckInPersistenceErrorClassifier
{
    public bool IsTemporarilyUnavailable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException
                && (sqlException.IsTransient || IsKnownInfrastructureFailure(sqlException.Number)))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsKnownInfrastructureFailure(int number) => number is
        // SqlClient uses error 0 for client-side connection and transport failures.
        // Server authentication and constraint failures retain their non-zero SQL numbers.
        0 or -2 or 20 or 53 or 64 or 121 or 233 or 258
        or 10053 or 10054 or 10060 or 10061
        or 10928 or 10929 or 1205
        or 40197 or 40501 or 40613
        or 49918 or 49919 or 49920;
}