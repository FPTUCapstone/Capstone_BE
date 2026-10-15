namespace TripMate.Application.Common.Interfaces;

/// <summary>
/// Serializes credential-mutating operations (e.g. UC-07 change password) for one account
/// inside the authoritative backend process. It does not provide cross-process coordination.
/// </summary>
public interface IAccountCredentialLock
{
    Task<T> ExecuteAsync<T>(
        long userId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);
}