using System.Collections.Concurrent;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Services;

/// <summary>
/// Process-local keyed lock for UC-06. The deployment contract permits one authoritative
/// backend process, so every request and confirm for an account shares this singleton gate.
/// </summary>
public sealed class InMemoryPasswordResetAccountLock : IPasswordResetAccountLock
{
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _accountGates = new();

    public async Task ExecuteAsync(
        long userId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var gate = GetGate(userId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await operation(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<T> ExecuteAsync<T>(
        long userId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var gate = GetGate(userId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim GetGate(long userId) =>
        _accountGates.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
}