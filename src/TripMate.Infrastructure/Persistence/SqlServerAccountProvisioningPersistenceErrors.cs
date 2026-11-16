using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.AccountProvisioning;

namespace TripMate.Infrastructure.Persistence;

public sealed class SqlServerAccountProvisioningPersistenceErrors : IAccountProvisioningPersistenceErrors
{
    public string? GetUniqueConflictCode(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is not SqlException { Number: 2601 or 2627 } sql) continue;
            if (sql.Message.Contains("'UX_Users_Email'", StringComparison.Ordinal)) return ProvisioningErrors.DuplicateEmail;
            if (sql.Message.Contains("'UX_Users_Phone'", StringComparison.Ordinal)) return ProvisioningErrors.DuplicatePhone;
            if (sql.Message.Contains("'UX_ProviderPoiLinks_Poi'", StringComparison.Ordinal)) return ProvisioningErrors.PoiAlreadyLinked;
        }

        return null;
    }
}