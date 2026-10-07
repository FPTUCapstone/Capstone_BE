using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Admin.Users.Unlock;

public sealed class UserUnlockDomainTests
{
    private static readonly DateTimeOffset LockedAtUtc = new(2026, 10, 7, 1, 30, 0, TimeSpan.Zero);

    [Fact]
    public void RestoreFromLock_WhenCompleteLockRecord_RestoresRecordedStatusAndClearsMetadata()
    {
        var user = new User
        {
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            FullName = "Locked traveler"
        };
        user.RecordLock(AccountStatus.Active, 7, LockedAtUtc, "Verified security incident");

        bool restored = user.TryRestoreFromLock(out AccountStatus restoredStatus);

        restored.Should().BeTrue();
        restoredStatus.Should().Be(AccountStatus.Active);
        user.Status.Should().Be(AccountStatus.Active);
        user.StatusBeforeLock.Should().BeNull();
        user.LockedByUserId.Should().BeNull();
        user.LockedAtUtc.Should().BeNull();
        user.LockReason.Should().BeNull();
    }

    [Fact]
    public void RestoreFromLock_WhenLegacyLockMetadataIsMissing_DoesNotGuessActive()
    {
        var user = new User
        {
            Role = UserRole.Traveler,
            Status = AccountStatus.Locked,
            FullName = "Legacy locked traveler"
        };

        bool restored = user.TryRestoreFromLock(out AccountStatus restoredStatus);

        restored.Should().BeFalse();
        restoredStatus.Should().Be(AccountStatus.Locked);
        user.Status.Should().Be(AccountStatus.Locked);
    }
}