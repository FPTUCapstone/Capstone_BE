using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Services;

/// <summary>
/// Process-local striped lock for UC-07. The deployment contract permits one authoritative
/// backend process, so every change-password attempt for an account shares a stable singleton
/// gate. The fixed stripe count bounds memory; unrelated accounts may occasionally share a
/// gate without weakening per-account serialization. Mirrors InMemoryPasswordResetAccountLock's
/// pattern for a different credential-mutating feature.
/// </summary>
public sealed class InMemoryAccountCredentialLock : IAccountCredentialLock
{
    internal const int DefaultStripeCount = 256;

    private readonly SemaphoreSlim[] _accountGates = Enumerable.Range(0, DefaultStripeCount)
        .Select(static _ => new SemaphoreSlim(1, 1))
        .ToArray();

    internal int AllocatedGateCount => _accountGates.Length;

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

    private SemaphoreSlim GetGate(long userId)
    {
        var stripeIndex = (int)(unchecked((ulong)userId) % (uint)_accountGates.Length);
        return _accountGates[stripeIndex];
    }
}