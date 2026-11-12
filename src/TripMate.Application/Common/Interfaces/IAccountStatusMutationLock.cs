namespace TripMate.Application.Common.Interfaces;

public interface IAccountStatusMutationLock
{
    Task AcquireSharedAsync(
        IReadOnlyCollection<long> accountIds,
        CancellationToken cancellationToken);

    Task AcquireExclusiveAsync(
        IReadOnlyCollection<long> accountIds,
        CancellationToken cancellationToken);
}