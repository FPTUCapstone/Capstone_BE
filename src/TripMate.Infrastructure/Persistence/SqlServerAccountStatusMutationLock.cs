using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Locks;

namespace TripMate.Infrastructure.Persistence;

internal sealed class SqlServerAccountStatusMutationLock(
    SqlServerTransactionApplicationLockExecutor executor)
    : IAccountStatusMutationLock
{
    private const string ResourceCategory = "AccountStatus";

    public Task AcquireSharedAsync(
        IReadOnlyCollection<long> accountIds,
        CancellationToken cancellationToken) =>
        AcquireAsync(accountIds, "Shared", cancellationToken);

    public Task AcquireExclusiveAsync(
        IReadOnlyCollection<long> accountIds,
        CancellationToken cancellationToken) =>
        AcquireAsync(accountIds, "Exclusive", cancellationToken);

    private Task AcquireAsync(
        IReadOnlyCollection<long> accountIds,
        string mode,
        CancellationToken cancellationToken)
    {
        var normalizedIds = AccountStatusLockResource.Normalize(accountIds);
        var resources = normalizedIds
            .Select(AccountStatusLockResource.Create)
            .ToArray();

        return executor.AcquireAsync(resources, mode, ResourceCategory, cancellationToken);
    }
}