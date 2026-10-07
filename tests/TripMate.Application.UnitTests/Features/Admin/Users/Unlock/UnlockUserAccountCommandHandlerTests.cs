using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Admin.Users.Unlock;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Admin.Users.Unlock;

public sealed class UnlockUserAccountCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    private readonly TestDbContext _dbContext = TestDbContext.Create();
    private readonly FakeCurrentUserService _currentUser = new()
    {
        UserId = 10,
        Role = nameof(UserRole.Administrator)
    };
    private readonly FakeDateTimeProvider _clock = new() { UtcNow = Now };

    [Fact]
    public async Task Handle_WhenCompleteTravelerLock_RestoresSavedStatusRevokesTokensAndWritesOneAudit()
    {
        User target = CreateLockedUser(20, AccountStatus.PendingApproval);
        _dbContext.Users.Add(target);
        _dbContext.RefreshTokens.AddRange(
            new RefreshToken
            {
                Id = 101,
                UserId = target.Id,
                TokenHash = "active-token",
                CreatedAtUtc = Now.AddDays(-1),
                ExpiresAtUtc = Now.AddDays(7)
            },
            new RefreshToken
            {
                Id = 102,
                UserId = target.Id,
                TokenHash = "already-revoked",
                CreatedAtUtc = Now.AddDays(-2),
                ExpiresAtUtc = Now.AddDays(7),
                RevokedAtUtc = Now.AddHours(-1)
            });
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var result = await CreateHandler().Handle(
            new UnlockUserAccountCommand(target.Id, "  Verified identity and access.  ", "unlock-001"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be(target.Id);
        result.Value.RestoredStatus.Should().Be(AccountStatus.PendingApproval);
        result.Value.UnlockedAtUtc.Should().Be(Now);
        target.Status.Should().Be(AccountStatus.PendingApproval);
        target.UpdatedAtUtc.Should().Be(Now);
        target.StatusBeforeLock.Should().BeNull();
        _dbContext.RefreshTokens.Single(token => token.Id == 101).RevokedAtUtc.Should().Be(Now);
        _dbContext.RefreshTokens.Single(token => token.Id == 102).RevokedAtUtc.Should().Be(Now.AddHours(-1));

        AuditLog audit = _dbContext.AuditLogs.Should().ContainSingle().Subject;
        audit.ActionType.Should().Be(AuditActionTypes.UserUnlock);
        audit.AffectedEntity.Should().Be(AuditEntityTypes.User);
        audit.AffectedEntityId.Should().Be(target.Id);
        audit.ActorUserId.Should().Be(_currentUser.UserId);
        audit.Result.Should().Be(AuditOutcome.Success);
        audit.Reason.Should().Be("Verified identity and access.");
        _dbContext.UserUnlockOperations.Should().ContainSingle(operation =>
            operation.AdministratorUserId == _currentUser.UserId
            && operation.TargetUserId == target.Id
            && operation.IdempotencyKey == "unlock-001");
    }

    [Fact]
    public async Task Handle_WhenSameKeyAndPayloadIsRetried_ReplaysOriginalResponseWithoutSecondMutation()
    {
        User target = CreateLockedUser(20, AccountStatus.Active);
        _dbContext.Users.Add(target);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var command = new UnlockUserAccountCommand(target.Id, "Verified", "unlock-002");
        var first = await CreateHandler().Handle(command, CancellationToken.None);
        _clock.UtcNow = Now.AddMinutes(5);

        var replay = await CreateHandler().Handle(command, CancellationToken.None);

        first.Should().BeEquivalentTo(replay);
        _dbContext.AuditLogs.Should().ContainSingle();
        _dbContext.UserUnlockOperations.Should().ContainSingle();
        target.Status.Should().Be(AccountStatus.Active);
    }

    [Fact]
    public async Task Handle_WhenSameKeyHasDifferentReason_ReturnsPayloadMismatchWithoutSecondAudit()
    {
        User target = CreateLockedUser(20, AccountStatus.Active);
        _dbContext.Users.Add(target);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        await CreateHandler().Handle(
            new UnlockUserAccountCommand(target.Id, "Verified", "unlock-003"),
            CancellationToken.None);

        var mismatch = await CreateHandler().Handle(
            new UnlockUserAccountCommand(target.Id, "Different evidence", "unlock-003"),
            CancellationToken.None);

        mismatch.IsFailure.Should().BeTrue();
        mismatch.ErrorCode.Should().Be("user.unlock_idempotency_key_payload_mismatch");
        _dbContext.AuditLogs.Should().ContainSingle();
        _dbContext.UserUnlockOperations.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_WhenTargetIsAdministrator_ReturnsProtectedFailure()
    {
        var administrator = new User
        {
            Id = 20,
            FullName = "Protected administrator",
            Role = UserRole.Administrator,
            Status = AccountStatus.Locked
        };
        _dbContext.Users.Add(administrator);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var result = await CreateHandler().Handle(
            new UnlockUserAccountCommand(administrator.Id, "Verified", "unlock-004"),
            CancellationToken.None);

        result.ErrorCode.Should().Be("user.unlock_protected_administrator");
        _dbContext.AuditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenTargetIsCallingAdministrator_ReturnsProtectedFailure()
    {
        var administrator = new User
        {
            Id = _currentUser.UserId!.Value,
            FullName = "Calling administrator",
            Role = UserRole.Administrator,
            Status = AccountStatus.Locked
        };
        _dbContext.Users.Add(administrator);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var result = await CreateHandler().Handle(
            new UnlockUserAccountCommand(administrator.Id, "Verified", "unlock-self"),
            CancellationToken.None);

        result.ErrorCode.Should().Be("user.unlock_protected_administrator");
        _dbContext.AuditLogs.Should().BeEmpty();
        _dbContext.UserUnlockOperations.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenTargetIsNotLocked_ReturnsConflictWithoutWritingOperation()
    {
        var target = new User
        {
            Id = 20,
            FullName = "Active traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active
        };
        _dbContext.Users.Add(target);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var result = await CreateHandler().Handle(
            new UnlockUserAccountCommand(target.Id, "Verified", "unlock-active"),
            CancellationToken.None);

        result.ErrorCode.Should().Be("user.not_locked");
        _dbContext.AuditLogs.Should().BeEmpty();
        _dbContext.UserUnlockOperations.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenLockRecoveryMetadataIsMissing_ReturnsConflictWithoutGuessingStatus()
    {
        var target = new User
        {
            Id = 20,
            FullName = "Legacy locked user",
            Role = UserRole.Traveler,
            Status = AccountStatus.Locked
        };
        _dbContext.Users.Add(target);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var result = await CreateHandler().Handle(
            new UnlockUserAccountCommand(target.Id, "Verified", "unlock-005"),
            CancellationToken.None);

        result.ErrorCode.Should().Be("user.lock_recovery_state_missing");
        target.Status.Should().Be(AccountStatus.Locked);
        _dbContext.AuditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenTargetDoesNotExist_ReturnsNotFound()
    {
        var result = await CreateHandler().Handle(
            new UnlockUserAccountCommand(404, "Verified", "unlock-006"),
            CancellationToken.None);

        result.ErrorCode.Should().Be("user.not_found");
        _dbContext.AuditLogs.Should().BeEmpty();
    }

    private UnlockUserAccountCommandHandler CreateHandler() =>
        new(_dbContext, _currentUser, _clock, new NoOpUserUnlockLock());

    private static User CreateLockedUser(long id, AccountStatus statusBeforeLock)
    {
        var user = new User
        {
            Id = id,
            FullName = "Locked traveler",
            Role = UserRole.Traveler,
            Status = statusBeforeLock
        };
        user.RecordLock(statusBeforeLock, 10, Now.AddHours(-2), "Security review");
        return user;
    }

    private sealed class NoOpUserUnlockLock : TripMate.Application.Common.Interfaces.IUserUnlockLock
    {
        public Task AcquireAsync(long administratorUserId, string idempotencyKey, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task AcquireTargetUserAsync(long targetUserId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}