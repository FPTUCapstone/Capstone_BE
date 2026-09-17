using TripMate.Application.Common.Interfaces;

namespace TripMate.Application.UnitTests.TestUtilities;

public sealed class FakeGroupJoinLock : IGroupJoinLock
{
    public List<string> AcquiredLocks { get; } = [];

    public Task AcquireOperationLockAsync(long travelerUserId, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        AcquiredLocks.Add($"Operation:{travelerUserId}:{idempotencyKey:N}");
        return Task.CompletedTask;
    }

    public Task AcquireCodeLockAsync(string normalizedInviteCode, CancellationToken cancellationToken)
    {
        AcquiredLocks.Add($"Code:{normalizedInviteCode}");
        return Task.CompletedTask;
    }

    public Task AcquireGroupLockAsync(long groupId, CancellationToken cancellationToken)
    {
        AcquiredLocks.Add($"Group:{groupId}");
        return Task.CompletedTask;
    }
}
