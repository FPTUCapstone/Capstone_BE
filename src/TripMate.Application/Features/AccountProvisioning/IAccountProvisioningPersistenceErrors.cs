using Microsoft.EntityFrameworkCore;

namespace TripMate.Application.Features.AccountProvisioning;

public interface IAccountProvisioningPersistenceErrors
{
    string? GetUniqueConflictCode(DbUpdateException exception);
}