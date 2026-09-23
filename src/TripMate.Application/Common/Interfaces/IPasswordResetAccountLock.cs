namespace TripMate.Application.Common.Interfaces;

/// <summary>
/// Serializes password-reset workflows for one account inside the authoritative backend
/// process. It does not provide cross-process coordination.
/// </summary>
public interface IPasswordResetAccountLock
{
    Task ExecuteAsync(
        long userId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken);

    Task<T> ExecuteAsync<T>(
        long userId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);
}