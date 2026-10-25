using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Tickets.Common;
using TripMate.Infrastructure.Persistence.Configurations;

namespace TripMate.Infrastructure.Persistence;

public sealed class SqlServerTicketQrPersistenceErrorClassifier : ITicketQrPersistenceErrorClassifier
{
    private static readonly string QuotedIndexName = $"'{TicketQrCodeConfiguration.OneActivePerTicketIndexName}'";

    public bool IsOneActiveCodeConflict(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException
                && sqlException.Number is 2601 or 2627
                && sqlException.Message.Contains(QuotedIndexName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
