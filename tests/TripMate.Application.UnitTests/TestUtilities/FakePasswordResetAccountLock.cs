using TripMate.Application.Common.Interfaces;

namespace TripMate.Application.UnitTests.TestUtilities;

internal sealed class FakePasswordResetAccountLock : IPasswordResetAccountLock
{
    public Task ExecuteAsync(
        long userId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken) => operation(cancellationToken);

    public Task<T> ExecuteAsync<T>(
        long userId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) => operation(cancellationToken);
}