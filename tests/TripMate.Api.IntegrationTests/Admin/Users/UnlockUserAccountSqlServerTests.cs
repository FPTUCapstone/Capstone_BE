using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.Users.Common;
using TripMate.Application.Features.Admin.Users.Unlock;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Admin.Users;

public sealed class UnlockUserAccountSqlServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task UnlockAndReplay_RevokesTokensAndPersistsExactlyOneAuditAndOperation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);

        UnlockUserAccountResponse first;
        await using (var context = database.CreateDbContext())
        {
            var handler = new UnlockUserAccountCommandHandler(
                context,
                new FixedCurrentUser(seed.AdministratorId),
                new FixedClock(),
                new SqlServerUserUnlockLock(context));
            var result = await handler.Handle(
                new UnlockUserAccountCommand(seed.TravelerId, "Verified support case", "sql-unlock-001"),
                CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            first = result.Value;
        }

        await using (var replayContext = database.CreateDbContext())
        {
            var handler = new UnlockUserAccountCommandHandler(
                replayContext,
                new FixedCurrentUser(seed.AdministratorId),
                new FixedClock(),
                new SqlServerUserUnlockLock(replayContext));
            var replay = await handler.Handle(
                new UnlockUserAccountCommand(seed.TravelerId, "Verified support case", "sql-unlock-001"),
                CancellationToken.None);
            replay.IsSuccess.Should().BeTrue();
            replay.Value.Should().BeEquivalentTo(first);
        }

        await using var verification = database.CreateDbContext();
        (await verification.Users.SingleAsync(user => user.Id == seed.TravelerId)).Status.Should()
            .Be(AccountStatus.Active);
        (await verification.RefreshTokens.SingleAsync(token => token.UserId == seed.TravelerId)).RevokedAtUtc.Should()
            .Be(Now);
        (await verification.AuditLogs.CountAsync(log => log.AffectedEntityId == seed.TravelerId)).Should().Be(1);
        (await verification.UserUnlockOperations.CountAsync(operation =>
            operation.AdministratorUserId == seed.AdministratorId && operation.TargetUserId == seed.TravelerId)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentSameKeyRequests_CreateOneAuditAndReplayOneResponse()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<UnlockUserAccountResponse> UnlockAsync()
        {
            await startGate.Task;
            await using var context = database.CreateDbContext();
            var handler = new UnlockUserAccountCommandHandler(
                context,
                new FixedCurrentUser(seed.AdministratorId),
                new FixedClock(),
                new SqlServerUserUnlockLock(context));
            var result = await handler.Handle(
                new UnlockUserAccountCommand(seed.TravelerId, "Verified support case", "sql-unlock-concurrent"),
                CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            return result.Value;
        }

        var first = UnlockAsync();
        var second = UnlockAsync();
        startGate.SetResult();
        var responses = await Task.WhenAll(first, second);

        responses[0].Should().BeEquivalentTo(responses[1]);
        await using var verification = database.CreateDbContext();
        (await verification.AuditLogs.CountAsync(log => log.AffectedEntityId == seed.TravelerId)).Should().Be(1);
        (await verification.UserUnlockOperations.CountAsync(operation =>
            operation.AdministratorUserId == seed.AdministratorId && operation.TargetUserId == seed.TravelerId)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentDifferentKeysForSameTarget_OneSucceedsAndOneReturnsNotLockedWithoutDuplicateAudit()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<TripMate.Application.Common.Models.Result<UnlockUserAccountResponse>> UnlockAsync(string idempotencyKey)
        {
            await startGate.Task;
            await using var context = database.CreateDbContext();
            var handler = new UnlockUserAccountCommandHandler(
                context,
                new FixedCurrentUser(seed.AdministratorId),
                new FixedClock(),
                new SqlServerUserUnlockLock(context));
            return await handler.Handle(
                new UnlockUserAccountCommand(seed.TravelerId, "Verified support case", idempotencyKey),
                CancellationToken.None);
        }

        var first = UnlockAsync("sql-unlock-target-a");
        var second = UnlockAsync("sql-unlock-target-b");
        startGate.SetResult();
        var results = await Task.WhenAll(first, second);

        results.Count(result => result.IsSuccess).Should().Be(1);
        var rejected = results.Single(result => result.IsFailure);
        rejected.ErrorCode.Should().Be(UserAdministrationErrorCodes.NotLocked);

        await using var verification = database.CreateDbContext();
        (await verification.Users.SingleAsync(user => user.Id == seed.TravelerId)).Status.Should()
            .Be(AccountStatus.Active);
        (await verification.AuditLogs.CountAsync(log => log.AffectedEntityId == seed.TravelerId)).Should().Be(1);
        (await verification.UserUnlockOperations.CountAsync(operation =>
            operation.AdministratorUserId == seed.AdministratorId && operation.TargetUserId == seed.TravelerId)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ReusedKeyWithDifferentReason_ReturnsConflictWithoutSecondMutation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);

        await using (var firstContext = database.CreateDbContext())
        {
            var handler = new UnlockUserAccountCommandHandler(
                firstContext,
                new FixedCurrentUser(seed.AdministratorId),
                new FixedClock(),
                new SqlServerUserUnlockLock(firstContext));
            (await handler.Handle(
                new UnlockUserAccountCommand(seed.TravelerId, "Verified support case", "sql-unlock-mismatch"),
                CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        await using (var replayContext = database.CreateDbContext())
        {
            var handler = new UnlockUserAccountCommandHandler(
                replayContext,
                new FixedCurrentUser(seed.AdministratorId),
                new FixedClock(),
                new SqlServerUserUnlockLock(replayContext));
            var mismatch = await handler.Handle(
                new UnlockUserAccountCommand(seed.TravelerId, "Different support evidence", "sql-unlock-mismatch"),
                CancellationToken.None);

            mismatch.IsFailure.Should().BeTrue();
            mismatch.ErrorCode.Should().Be("user.unlock_idempotency_key_payload_mismatch");
        }

        await using var verification = database.CreateDbContext();
        (await verification.AuditLogs.CountAsync(log => log.AffectedEntityId == seed.TravelerId)).Should().Be(1);
        (await verification.UserUnlockOperations.CountAsync(operation =>
            operation.AdministratorUserId == seed.AdministratorId && operation.TargetUserId == seed.TravelerId)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task PersistenceFailure_RollsBackUserTokenAuditAndOperation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);

        await using (var failingContext = database.CreateDbContext(new FailingSaveChangesInterceptor()))
        {
            var handler = new UnlockUserAccountCommandHandler(
                failingContext,
                new FixedCurrentUser(seed.AdministratorId),
                new FixedClock(),
                new SqlServerUserUnlockLock(failingContext));

            Func<Task> action = () => handler.Handle(
                new UnlockUserAccountCommand(seed.TravelerId, "Verified support case", "sql-unlock-rollback"),
                CancellationToken.None);

            await action.Should().ThrowAsync<DbUpdateException>();
        }

        await using var verification = database.CreateDbContext();
        (await verification.Users.SingleAsync(user => user.Id == seed.TravelerId)).Status.Should()
            .Be(AccountStatus.Locked);
        (await verification.RefreshTokens.SingleAsync(token => token.UserId == seed.TravelerId)).RevokedAtUtc.Should()
            .BeNull();
        (await verification.AuditLogs.CountAsync(log => log.AffectedEntityId == seed.TravelerId)).Should().Be(0);
        (await verification.UserUnlockOperations.CountAsync()).Should().Be(0);
    }

    private static async Task<(long AdministratorId, long TravelerId)> SeedAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var administrator = new User
        {
            FullName = "SQL unlock administrator",
            Email = $"unlock-admin-{Guid.NewGuid():N}@example.com",
            Role = UserRole.Administrator,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        context.Users.Add(administrator);
        await context.SaveChangesAsync();

        var traveler = new User
        {
            FullName = "SQL locked traveler",
            Email = $"unlock-traveler-{Guid.NewGuid():N}@example.com",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        traveler.RecordLock(AccountStatus.Active, administrator.Id, Now.AddHours(-1), "Security review");
        context.Users.Add(traveler);
        await context.SaveChangesAsync();

        context.RefreshTokens.Add(new RefreshToken
        {
            UserId = traveler.Id,
            TokenHash = "sql-unlock-active-token",
            CreatedAtUtc = Now.AddHours(-2),
            ExpiresAtUtc = Now.AddDays(7),
        });
        await context.SaveChangesAsync();
        return (administrator.Id, traveler.Id);
    }

    private sealed class FixedCurrentUser(long userId) : ICurrentUserService
    {
        public long? UserId { get; } = userId;

        public string? Role { get; } = nameof(UserRole.Administrator);
    }

    private sealed class FixedClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }

    private sealed class FailingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("Simulated persistence failure during account unlock.");
    }
}