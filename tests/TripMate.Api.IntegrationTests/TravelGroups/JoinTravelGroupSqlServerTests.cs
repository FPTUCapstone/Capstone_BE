using System.Text.RegularExpressions;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Application.Features.TravelGroups.GetInvitation;
using TripMate.Application.Features.TravelGroups.JoinTravelGroup;
using TripMate.Application.Features.TravelGroups.ManageInvitation;
using TripMate.Domain.Constants;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class JoinTravelGroupSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentRequestsWithSameKey_CreateOneMembershipAndReplaySameResult()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var key = Guid.NewGuid();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<JoinTravelGroupResponse> JoinAsync()
        {
            await startGate.Task;
            await using var context = database.CreateDbContext();
            var handler = new JoinTravelGroupCommandHandler(
                context,
                new FixedDateTimeProvider(),
                new SqlServerGroupJoinLock(context));
            var result = await handler.Handle(
                new JoinTravelGroupCommand(seed.InviteCode, seed.TravelerUserId, key),
                CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            return result.Value;
        }

        var first = JoinAsync();
        var second = JoinAsync();
        startGate.SetResult();
        var responses = await Task.WhenAll(first, second);

        responses[0].GroupId.Should().Be(seed.GroupId);
        responses[1].GroupId.Should().Be(seed.GroupId);
        responses[0].GroupName.Should().Be(responses[1].GroupName);

        await using var verification = database.CreateDbContext();
        var memberCount = await verification.GroupMembers.CountAsync(
            member => member.GroupId == seed.GroupId && member.UserId == seed.TravelerUserId);
        memberCount.Should().Be(1);

        var opCount = await verification.GroupJoinOperations.CountAsync(
            op => op.TravelerUserId == seed.TravelerUserId && op.IdempotencyKey == key);
        opCount.Should().Be(1);

        var invitation = await verification.GroupInvitations.FirstAsync(
            inv => inv.GroupId == seed.GroupId);
        invitation.UsedCount.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentRequestsWithDifferentKeys_CreateExactlyOneMembership()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Application.Common.Models.Result<JoinTravelGroupResponse>> JoinAsync()
        {
            await startGate.Task;
            await using var context = database.CreateDbContext();
            var handler = new JoinTravelGroupCommandHandler(
                context,
                new FixedDateTimeProvider(),
                new SqlServerGroupJoinLock(context));
            return await handler.Handle(
                new JoinTravelGroupCommand(seed.InviteCode, seed.TravelerUserId, Guid.NewGuid()),
                CancellationToken.None);
        }

        var first = JoinAsync();
        var second = JoinAsync();
        startGate.SetResult();
        var results = await Task.WhenAll(first, second);

        var successes = results.Count(r => r.IsSuccess);
        var failures = results.Count(r => r.IsFailure);

        successes.Should().Be(1);
        failures.Should().Be(1);

        var failedResult = results.First(r => r.IsFailure);
        failedResult.ErrorCode.Should().Be(TravelGroupErrorCodes.AlreadyActiveMember);
        failedResult.ErrorMetadata.Should().ContainKey("groupId");
        failedResult.ErrorMetadata["groupId"].Should().Be(seed.GroupId);

        await using var verification = database.CreateDbContext();
        var memberCount = await verification.GroupMembers.CountAsync(
            member => member.GroupId == seed.GroupId && member.UserId == seed.TravelerUserId);
        memberCount.Should().Be(1);

        var opCount = await verification.GroupJoinOperations.CountAsync(
            op => op.TravelerUserId == seed.TravelerUserId);
        opCount.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task PayloadMismatch_ReusedKeyWithDifferentCode_Returns409AndNoMutation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var key = Guid.NewGuid();

        await using (var firstContext = database.CreateDbContext())
        {
            var handler = new JoinTravelGroupCommandHandler(
                firstContext,
                new FixedDateTimeProvider(),
                new SqlServerGroupJoinLock(firstContext));
            var firstResult = await handler.Handle(
                new JoinTravelGroupCommand(seed.InviteCode, seed.TravelerUserId, key),
                CancellationToken.None);
            firstResult.IsSuccess.Should().BeTrue();
        }

        await using (var mismatchContext = database.CreateDbContext())
        {
            var handler = new JoinTravelGroupCommandHandler(
                mismatchContext,
                new FixedDateTimeProvider(),
                new SqlServerGroupJoinLock(mismatchContext));
            var mismatchResult = await handler.Handle(
                new JoinTravelGroupCommand("DIFFCODE", seed.TravelerUserId, key),
                CancellationToken.None);
            mismatchResult.IsFailure.Should().BeTrue();
            mismatchResult.ErrorCode.Should().Be(TravelGroupErrorCodes.IdempotencyKeyPayloadMismatch);
        }

        await using var verification = database.CreateDbContext();
        (await verification.GroupJoinOperations.CountAsync(
            op => op.TravelerUserId == seed.TravelerUserId)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Join_WhenCallerWasRemoved_ReactivatesMembershipAndResetsLocationSharing()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var now = new FixedDateTimeProvider().UtcNow;

        await using (var setup = database.CreateDbContext())
        {
            var group = await setup.TravelGroups.FirstAsync(g => g.Id == seed.GroupId);
            var member = GroupMember.CreateMember(group, seed.TravelerUserId, now.AddDays(-5));
            typeof(GroupMember).GetProperty(nameof(GroupMember.Status))!
                .SetValue(member, GroupMemberStatus.Removed);
            typeof(GroupMember).GetProperty(nameof(GroupMember.LeftAtUtc))!
                .SetValue(member, now.AddDays(-2));
            typeof(GroupMember).GetProperty(nameof(GroupMember.LocationSharingEnabled))!
                .SetValue(member, true);
            setup.GroupMembers.Add(member);
            await setup.SaveChangesAsync();
        }

        await using (var context = database.CreateDbContext())
        {
            var handler = new JoinTravelGroupCommandHandler(
                context,
                new FixedDateTimeProvider(),
                new SqlServerGroupJoinLock(context));
            var result = await handler.Handle(
                new JoinTravelGroupCommand(seed.InviteCode, seed.TravelerUserId, Guid.NewGuid()),
                CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
        }

        await using var verification = database.CreateDbContext();
        var reactivatedMember = await verification.GroupMembers.FirstAsync(
            m => m.GroupId == seed.GroupId && m.UserId == seed.TravelerUserId);
        reactivatedMember.Status.Should().Be(GroupMemberStatus.Active);
        reactivatedMember.LocationSharingEnabled.Should().BeFalse();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task HostRegeneratesInvitationWhileTravelerJoins_MaintainsConsistency()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Application.Common.Models.Result<JoinTravelGroupResponse>> JoinAsync()
        {
            await startGate.Task;
            await using var context = database.CreateDbContext();
            var handler = new JoinTravelGroupCommandHandler(
                context,
                new FixedDateTimeProvider(),
                new SqlServerGroupJoinLock(context));
            return await handler.Handle(
                new JoinTravelGroupCommand(seed.InviteCode, seed.TravelerUserId, Guid.NewGuid()),
                CancellationToken.None);
        }

        async Task<Application.Common.Models.Result<GetGroupInvitationResponse>> RegenerateAsync()
        {
            await startGate.Task;
            await using var context = database.CreateDbContext();
            var handler = new RegenerateGroupInvitationCommandHandler(
                context,
                new FixedDateTimeProvider(),
                new SqlServerGroupInvitationLock(context),
                new RandomGroupInvitationCodeGenerator());
            return await handler.Handle(
                new RegenerateGroupInvitationCommand(seed.GroupId, seed.HostUserId, Guid.NewGuid()),
                CancellationToken.None);
        }

        var joinTask = JoinAsync();
        var regenTask = RegenerateAsync();
        startGate.SetResult();
        await Task.WhenAll(joinTask, regenTask);

        var joinResult = await joinTask;
        var regenResult = await regenTask;

        regenResult.IsSuccess.Should().BeTrue();
        if (joinResult.IsSuccess)
        {
            await using var verification = database.CreateDbContext();
            (await verification.GroupMembers.CountAsync(
                m => m.GroupId == seed.GroupId && m.UserId == seed.TravelerUserId)).Should().Be(1);
        }
        else
        {
            joinResult.ErrorCode.Should().Be(TravelGroupErrorCodes.InvitationUnavailable);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task JoinOperationMigration_WhenTableIsPartiallyCreated_CompletesInvariantsAndCanRunTwice()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        long invitationId;

        await using (var setup = database.CreateDbContext())
        {
            var inv = await setup.GroupInvitations.FirstAsync(i => i.GroupId == seed.GroupId);
            invitationId = inv.Id;
        }

        await database.ExecuteNonQueryAsync($"""
            DROP TABLE social.GroupJoinOperations;
            CREATE TABLE social.GroupJoinOperations (
                join_operation_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                traveler_user_id BIGINT NOT NULL,
                group_id BIGINT NOT NULL,
                invitation_id BIGINT NOT NULL,
                invitation_code VARCHAR(20) NOT NULL,
                idempotency_key UNIQUEIDENTIFIER NOT NULL,
                created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
            );
            INSERT INTO social.GroupJoinOperations
                (traveler_user_id, group_id, invitation_id, invitation_code, idempotency_key)
            VALUES ({seed.TravelerUserId}, {seed.GroupId}, {invitationId}, '{seed.InviteCode}', '{Guid.NewGuid()}');
            """);

        await ApplyJoinOperationMigrationAsync(database);
        await ApplyJoinOperationMigrationAsync(database);

        await database.ExecuteNonQueryAsync("""
            IF OBJECT_ID(N'social.GroupJoinOperations', N'U') IS NULL
                THROW 51000, 'Migration did not retain the operation table.', 1;

            IF (SELECT COUNT(*) FROM social.GroupJoinOperations) <> 1
                THROW 51000, 'Migration did not preserve existing operation data.', 1;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.foreign_key_columns AS foreignKeyColumn
                WHERE foreignKeyColumn.parent_object_id = OBJECT_ID(N'social.GroupJoinOperations')
                  AND foreignKeyColumn.referenced_object_id = OBJECT_ID(N'dbo.Users'))
                THROW 51000, 'Migration did not add the traveler foreign key.', 1;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.foreign_key_columns AS foreignKeyColumn
                WHERE foreignKeyColumn.parent_object_id = OBJECT_ID(N'social.GroupJoinOperations')
                  AND foreignKeyColumn.referenced_object_id = OBJECT_ID(N'social.TravelGroups'))
                THROW 51000, 'Migration did not add the group foreign key.', 1;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.foreign_key_columns AS foreignKeyColumn
                WHERE foreignKeyColumn.parent_object_id = OBJECT_ID(N'social.GroupJoinOperations')
                  AND foreignKeyColumn.referenced_object_id = OBJECT_ID(N'social.GroupInvitations'))
                THROW 51000, 'Migration did not add the invitation foreign key.', 1;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes AS [index]
                WHERE [index].object_id = OBJECT_ID(N'social.GroupJoinOperations')
                  AND [index].is_unique = 1)
                THROW 51000, 'Migration did not add the traveler/idempotency unique constraint.', 1;
            """);
    }

    private static async Task<(long HostUserId, long TravelerUserId, long GroupId, string InviteCode)> SeedAsync(
        SqlServerTestDatabase database,
        string inviteCode = "JOINTEST")
    {
        await using var context = database.CreateDbContext();
        var now = new FixedDateTimeProvider().UtcNow;
        var host = new User
        {
            Email = $"sql-join-host-{Guid.NewGuid():N}@example.com",
            FullName = "SQL Join Host",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var traveler = new User
        {
            Email = $"sql-join-traveler-{Guid.NewGuid():N}@example.com",
            FullName = "SQL Join Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.Users.AddRange(host, traveler);
        await context.SaveChangesAsync();

        var itinerary = Itinerary.CreateManual(host.Id, "SQL Join Trip", Itinerary.ActiveStatus, now);
        context.Itineraries.Add(itinerary);
        await context.SaveChangesAsync();

        var group = TravelGroup.Create(itinerary.Id, host.Id, "SQL Join Group", now);
        context.TravelGroups.Add(group);
        await context.SaveChangesAsync();

        var invitation = GroupInvitation.Create(
            group.Id,
            host.Id,
            inviteCode,
            now.AddDays(TravelGroupConstants.InvitationCodeExpiryDays),
            TravelGroupConstants.UnlimitedInvitationUses,
            now);
        context.GroupInvitations.Add(invitation);
        await context.SaveChangesAsync();

        return (host.Id, traveler.Id, group.Id, inviteCode);
    }

    private static async Task ApplyJoinOperationMigrationAsync(SqlServerTestDatabase database)
    {
        var migrationPath = Path.Combine(
            AppContext.BaseDirectory,
            "Database",
            "migrations",
            "20260917_add_group_join_operations.sql");
        var migration = await File.ReadAllTextAsync(migrationPath);
        migration = Regex.Replace(migration, @"^\s*GO\s*$", string.Empty, RegexOptions.Multiline);
        await database.ExecuteNonQueryAsync(migration);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task PersistenceFailure_RollsBackTransaction_LeavingNoMembershipUsageOrOperation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var key = Guid.NewGuid();

        await using (var failingContext = database.CreateDbContext(new FailingSaveChangesInterceptor()))
        {
            var handler = new JoinTravelGroupCommandHandler(
                failingContext,
                new FixedDateTimeProvider(),
                new SqlServerGroupJoinLock(failingContext));

            var act = () => handler.Handle(
                new JoinTravelGroupCommand(seed.InviteCode, seed.TravelerUserId, key),
                CancellationToken.None);

            await act.Should().ThrowAsync<DbUpdateException>();
        }

        // Verify across real SQL Server connection that the transaction rolled back cleanly
        await using var verification = database.CreateDbContext();

        var memberCount = await verification.GroupMembers.CountAsync(
            member => member.GroupId == seed.GroupId && member.UserId == seed.TravelerUserId);
        memberCount.Should().Be(0);

        var invitation = await verification.GroupInvitations.FirstAsync(
            inv => inv.GroupId == seed.GroupId);
        invitation.UsedCount.Should().Be(0);

        var opCount = await verification.GroupJoinOperations.CountAsync(
            op => op.TravelerUserId == seed.TravelerUserId && op.IdempotencyKey == key);
        opCount.Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task UnresolvedTargetGroupId_FailsFastWithoutDeadlockOrStateMutation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var nonexistentCode = "NONEXIST";
        var key = Guid.NewGuid();

        await using (var context = database.CreateDbContext())
        {
            var handler = new JoinTravelGroupCommandHandler(
                context,
                new FixedDateTimeProvider(),
                new SqlServerGroupJoinLock(context));

            var result = await handler.Handle(
                new JoinTravelGroupCommand(nonexistentCode, seed.TravelerUserId, key),
                CancellationToken.None);

            result.IsSuccess.Should().BeFalse();
            result.ErrorCode.Should().Be(TravelGroupErrorCodes.InvitationUnavailable);
        }

        // Verify across real SQL Server connection that no database mutation occurred
        await using var verification = database.CreateDbContext();

        var memberCount = await verification.GroupMembers.CountAsync(
            member => member.GroupId == seed.GroupId && member.UserId == seed.TravelerUserId);
        memberCount.Should().Be(0);

        var invitation = await verification.GroupInvitations.FirstAsync(
            inv => inv.GroupId == seed.GroupId);
        invitation.UsedCount.Should().Be(0);

        var opCount = await verification.GroupJoinOperations.CountAsync(
            op => op.TravelerUserId == seed.TravelerUserId && op.IdempotencyKey == key);
        opCount.Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task UnresolvedTargetGroupIdRace_WithConcurrentHostRegeneration_PreservesConsistencyAndNoDeadlock()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var nonexistentCode = "UNRESOLV";
        var key = Guid.NewGuid();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Application.Common.Models.Result<JoinTravelGroupResponse>> JoinUnresolvedAsync()
        {
            await startGate.Task;
            await using var context = database.CreateDbContext();
            var handler = new JoinTravelGroupCommandHandler(
                context,
                new FixedDateTimeProvider(),
                new SqlServerGroupJoinLock(context));
            return await handler.Handle(
                new JoinTravelGroupCommand(nonexistentCode, seed.TravelerUserId, key),
                CancellationToken.None);
        }

        async Task<Application.Common.Models.Result<GetGroupInvitationResponse>> RegenerateAsync()
        {
            await startGate.Task;
            await using var context = database.CreateDbContext();
            var handler = new RegenerateGroupInvitationCommandHandler(
                context,
                new FixedDateTimeProvider(),
                new SqlServerGroupInvitationLock(context),
                new RandomGroupInvitationCodeGenerator());
            return await handler.Handle(
                new RegenerateGroupInvitationCommand(seed.GroupId, seed.HostUserId, Guid.NewGuid()),
                CancellationToken.None);
        }

        var joinTask = JoinUnresolvedAsync();
        var regenTask = RegenerateAsync();
        startGate.SetResult();
        await Task.WhenAll(joinTask, regenTask);

        var joinResult = await joinTask;
        var regenResult = await regenTask;

        joinResult.IsSuccess.Should().BeFalse();
        joinResult.ErrorCode.Should().Be(TravelGroupErrorCodes.InvitationUnavailable);

        regenResult.IsSuccess.Should().BeTrue();

        await using var verification = database.CreateDbContext();
        var memberCount = await verification.GroupMembers.CountAsync(
            m => m.GroupId == seed.GroupId && m.UserId == seed.TravelerUserId);
        memberCount.Should().Be(0);

        var opCount = await verification.GroupJoinOperations.CountAsync(
            op => op.TravelerUserId == seed.TravelerUserId && op.IdempotencyKey == key);
        opCount.Should().Be(0);
    }

    private sealed class FailingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            throw new DbUpdateException("Simulated SQL Server persistence failure during join transaction.");
        }
    }

    private sealed class FixedDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
    }
}