namespace TripMate.Application.Common.Interfaces;

/// <summary>
/// Serializes credential-mutating operations (e.g. UC-07 change password) for one account
/// across every backend process/container. Its lifetime is the surrounding serializable
/// database transaction — the lock is released automatically on commit or rollback.
/// </summary>
public interface IAccountCredentialLock
{
    Task AcquireAsync(long userId, CancellationToken cancellationToken);
}