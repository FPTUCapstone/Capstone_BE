using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Application.Features.TravelGroups.JoinTravelGroup;
using TripMate.Application.Features.TravelGroups.LeaveTravelGroup;
using TripMate.Application.Features.TravelGroups.ManageInvitation;
using TripMate.Application.Features.TravelGroups.RemoveGroupMember;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class RemoveGroupMemberSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task DatabaseEngine_IsSqlServer2022()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        (await database.ExecuteScalarAsync<int>(
            "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int)"))
            .Should().Be(16);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_IsRepeatableAndEnforcesUniqueHostKey()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var migration = Path.Combine(AppContext.BaseDirectory, "Database", "migrations",
            "20261027_uc20_group_member_removal_operations.sql");
        await database.ExecuteScriptAsync(migration);
        await database.ExecuteScriptAsync(migration);

        await using var context = database.CreateDbContext();
        var now = new FixedClock().UtcNow;
        var key = Guid.NewGuid();
        context.GroupMemberRemovalOperations.Add(GroupMemberRemovalOperation.Create(
            seed.HostId, seed.GroupId, seed.MemberId, "Member", key, now));
        await context.SaveChangesAsync();
        context.GroupMemberRemovalOperations.Add(GroupMemberRemovalOperation.Create(
            seed.HostId, seed.GroupId, seed.MemberId, "Member", key, now.AddMinutes(1)));
        var duplicate = () => context.SaveChangesAsync();
        await duplicate.Should().ThrowAsync<DbUpdateException>();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task NotificationWriteFailure_RollsBackMembershipAuditAndOperation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE dbo.Notifications
            ADD CONSTRAINT CK_UC20_RejectRemovalNotice
            CHECK ([type] <> 'TravelGroupMemberRemoved');
            """);

        await using (var context = database.CreateDbContext())
        {
            var action = () => CreateHandler(context).Handle(
                new(seed.GroupId, seed.MemberId, seed.HostId, Guid.NewGuid()), CancellationToken.None);
            await action.Should().ThrowAsync<DbUpdateException>();
        }

        await using var verification = database.CreateDbContext();
        (await verification.GroupMembers.SingleAsync(member => member.GroupId == seed.GroupId
            && member.UserId == seed.MemberId)).Status.Should().Be(GroupMemberStatus.Active);
        (await verification.GroupMemberRemovalOperations.CountAsync()).Should().Be(0);
        (await verification.AuditLogs.CountAsync()).Should().Be(0);
        (await verification.Notifications.CountAsync()).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RetryAfterRejoin_ReplaysWithoutRemovingAgain()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var key = Guid.NewGuid();
        RemoveGroupMemberResponse first;
        await using (var context = database.CreateDbContext())
        {
            var result = await CreateHandler(context).Handle(
                new(seed.GroupId, seed.MemberId, seed.HostId, key), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            first = result.Value;
        }
        await using (var context = database.CreateDbContext())
        {
            var member = await context.GroupMembers.SingleAsync(item =>
                item.GroupId == seed.GroupId && item.UserId == seed.MemberId);
            member.Reactivate(new FixedClock().UtcNow.AddHours(1));
            await context.SaveChangesAsync();
        }
        await using (var context = database.CreateDbContext())
        {
            var replay = await CreateHandler(context).Handle(
                new(seed.GroupId, seed.MemberId, seed.HostId, key), CancellationToken.None);
            replay.IsSuccess.Should().BeTrue();
            replay.Value.Should().Be(first);
        }
        await using var verification = database.CreateDbContext();
        (await verification.GroupMembers.SingleAsync(item => item.GroupId == seed.GroupId
            && item.UserId == seed.MemberId)).Status.Should().Be(GroupMemberStatus.Active);
        (await verification.GroupMemberRemovalOperations.CountAsync()).Should().Be(1);
        (await verification.AuditLogs.CountAsync()).Should().Be(1);
        (await verification.Notifications.CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentSameKey_RemovesOnceAndReplaysOneOutcome()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var key = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<RemoveGroupMemberResponse> RemoveAsync()
        {
            await gate.Task;
            await using var context = database.CreateDbContext();
            var result = await CreateHandler(context).Handle(
                new(seed.GroupId, seed.MemberId, seed.HostId, key), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            return result.Value;
        }

        var first = RemoveAsync();
        var second = RemoveAsync();
        gate.SetResult();
        var responses = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
        responses[0].Should().Be(responses[1]);

        await using var verification = database.CreateDbContext();
        (await verification.GroupMemberRemovalOperations.CountAsync()).Should().Be(1);
        (await verification.AuditLogs.CountAsync()).Should().Be(1);
        (await verification.Notifications.CountAsync()).Should().Be(1);
        (await verification.GroupMembers.SingleAsync(member => member.GroupId == seed.GroupId
            && member.UserId == seed.MemberId)).Status.Should().Be(GroupMemberStatus.Removed);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentDifferentKeys_OnlyOneRemovalCommits()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Application.Common.Models.Result<RemoveGroupMemberResponse>> RemoveAsync()
        {
            await gate.Task;
            await using var context = database.CreateDbContext();
            return await CreateHandler(context).Handle(
                new(seed.GroupId, seed.MemberId, seed.HostId, Guid.NewGuid()), CancellationToken.None);
        }

        var first = RemoveAsync();
        var second = RemoveAsync();
        gate.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
        results.Count(result => result.IsSuccess).Should().Be(1);
        results.Single(result => result.IsFailure).ErrorCode.Should().Be(TravelGroupErrorCodes.MemberNotActive);

        await using var verification = database.CreateDbContext();
        (await verification.GroupMemberRemovalOperations.CountAsync()).Should().Be(1);
        (await verification.AuditLogs.CountAsync()).Should().Be(1);
        (await verification.Notifications.CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RemoveRacingTargetLeave_CommitsOnlyOneMembershipTransition()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Application.Common.Models.Result<RemoveGroupMemberResponse>> RemoveAsync()
        {
            await gate.Task;
            await using var context = database.CreateDbContext();
            return await CreateHandler(context).Handle(
                new(seed.GroupId, seed.MemberId, seed.HostId, Guid.NewGuid()), CancellationToken.None);
        }

        async Task<Application.Common.Models.Result<LeaveTravelGroupResponse>> LeaveAsync()
        {
            await gate.Task;
            await using var context = database.CreateDbContext();
            return await new LeaveTravelGroupCommandHandler(
                context, new FixedClock(), new SqlServerGroupLeaveLock(context),
                new ItineraryAccessService(context),
                NullLogger<LeaveTravelGroupCommandHandler>.Instance).Handle(
                new(seed.GroupId, seed.MemberId, Guid.NewGuid()), CancellationToken.None);
        }

        var removal = RemoveAsync();
        var leave = LeaveAsync();
        gate.SetResult();
        await Task.WhenAll(removal, leave).WaitAsync(TimeSpan.FromSeconds(30));
        var removeResult = await removal;
        var leaveResult = await leave;
        (removeResult.IsSuccess ^ leaveResult.IsSuccess).Should().BeTrue();
        removeResult.IsFailure.Should().Be(!leaveResult.IsFailure);

        await using var verification = database.CreateDbContext();
        var member = await verification.GroupMembers.SingleAsync(item => item.GroupId == seed.GroupId
            && item.UserId == seed.MemberId);
        member.Status.Should().Be(removeResult.IsSuccess ? GroupMemberStatus.Removed : GroupMemberStatus.Left);
        (await verification.GroupMemberRemovalOperations.CountAsync()).Should().Be(removeResult.IsSuccess ? 1 : 0);
        (await verification.GroupLeaveOperations.CountAsync()).Should().Be(leaveResult.IsSuccess ? 1 : 0);
        (await verification.Notifications.CountAsync()).Should().Be(removeResult.IsSuccess ? 1 : 0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RemoveRacingRejoin_PreservesCommittedOrderAndSingleNotice()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var code = $"U{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        await using (var context = database.CreateDbContext())
        {
            var now = new FixedClock().UtcNow;
            context.GroupInvitations.Add(GroupInvitation.Create(
                seed.GroupId, seed.HostId, code, now.AddHours(1), 3, now));
            await context.SaveChangesAsync();
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Application.Common.Models.Result<RemoveGroupMemberResponse>> RemoveAsync()
        {
            await gate.Task;
            await using var context = database.CreateDbContext();
            return await CreateHandler(context).Handle(
                new(seed.GroupId, seed.MemberId, seed.HostId, Guid.NewGuid()), CancellationToken.None);
        }

        async Task<Application.Common.Models.Result<JoinTravelGroupResponse>> JoinAsync()
        {
            await gate.Task;
            await using var context = database.CreateDbContext();
            return await new JoinTravelGroupCommandHandler(
                context, new FixedClock(), new SqlServerGroupJoinLock(context)).Handle(
                new(code, seed.MemberId, Guid.NewGuid()), CancellationToken.None);
        }

        var removal = RemoveAsync();
        var join = JoinAsync();
        gate.SetResult();
        await Task.WhenAll(removal, join).WaitAsync(TimeSpan.FromSeconds(30));
        (await removal).IsSuccess.Should().BeTrue();
        var joinResult = await join;
        if (joinResult.IsFailure)
        {
            joinResult.ErrorCode.Should().Be(TravelGroupErrorCodes.AlreadyActiveMember);
        }

        await using var verification = database.CreateDbContext();
        (await verification.GroupMembers.SingleAsync(item => item.GroupId == seed.GroupId
            && item.UserId == seed.MemberId)).Status.Should().Be(
                joinResult.IsSuccess ? GroupMemberStatus.Active : GroupMemberStatus.Removed);
        (await verification.GroupMemberRemovalOperations.CountAsync()).Should().Be(1);
        (await verification.Notifications.CountAsync()).Should().Be(1);
        (await verification.GroupJoinOperations.CountAsync()).Should().Be(joinResult.IsSuccess ? 1 : 0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RemoveRacingHostLeave_NeverLeavesGroupWithInactiveHost()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Application.Common.Models.Result<RemoveGroupMemberResponse>> RemoveAsync()
        {
            await gate.Task;
            await using var context = database.CreateDbContext();
            return await CreateHandler(context).Handle(
                new(seed.GroupId, seed.MemberId, seed.HostId, Guid.NewGuid()), CancellationToken.None);
        }

        async Task<Application.Common.Models.Result<LeaveTravelGroupResponse>> LeaveAsync()
        {
            await gate.Task;
            await using var context = database.CreateDbContext();
            return await new LeaveTravelGroupCommandHandler(
                context, new FixedClock(), new SqlServerGroupLeaveLock(context),
                new ItineraryAccessService(context),
                NullLogger<LeaveTravelGroupCommandHandler>.Instance).Handle(
                new(seed.GroupId, seed.HostId, Guid.NewGuid()), CancellationToken.None);
        }

        var removal = RemoveAsync();
        var leave = LeaveAsync();
        gate.SetResult();
        await Task.WhenAll(removal, leave).WaitAsync(TimeSpan.FromSeconds(30));
        (await leave).IsSuccess.Should().BeTrue();
        var removed = await removal;
        if (removed.IsFailure)
        {
            removed.ErrorCode.Should().Be(TravelGroupErrorCodes.HostPermissionRequired);
        }

        await using var verification = database.CreateDbContext();
        var group = await verification.TravelGroups.SingleAsync(item => item.Id == seed.GroupId);
        var active = await verification.GroupMembers
            .Where(item => item.GroupId == seed.GroupId && item.Status == GroupMemberStatus.Active)
            .Select(item => item.UserId).ToListAsync();
        if (removed.IsSuccess)
        {
            group.Status.Should().Be(TravelGroupStatus.Closed);
            active.Should().BeEmpty();
        }
        else
        {
            group.Status.Should().Be(TravelGroupStatus.Active);
            group.HostUserId.Should().Be(seed.MemberId);
            active.Should().Equal(seed.MemberId);
        }

        (await verification.GroupMemberRemovalOperations.CountAsync()).Should().Be(removed.IsSuccess ? 1 : 0);
        (await verification.Notifications.CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RemoveRacingInvitationRegeneration_PreservesMembershipAndInvitation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Application.Common.Models.Result<RemoveGroupMemberResponse>> RemoveAsync()
        {
            await gate.Task;
            await using var context = database.CreateDbContext();
            return await CreateHandler(context).Handle(
                new(seed.GroupId, seed.MemberId, seed.HostId, Guid.NewGuid()), CancellationToken.None);
        }

        async Task<Application.Common.Models.Result<Application.Features.TravelGroups.GetInvitation.GetGroupInvitationResponse>> RegenerateAsync()
        {
            await gate.Task;
            await using var context = database.CreateDbContext();
            return await new RegenerateGroupInvitationCommandHandler(
                context, new FixedClock(), new SqlServerGroupInvitationLock(context),
                new RandomGroupInvitationCodeGenerator()).Handle(
                new(seed.GroupId, seed.HostId, Guid.NewGuid()), CancellationToken.None);
        }

        var removal = RemoveAsync();
        var regeneration = RegenerateAsync();
        gate.SetResult();
        await Task.WhenAll(removal, regeneration).WaitAsync(TimeSpan.FromSeconds(30));
        (await removal).IsSuccess.Should().BeTrue();
        (await regeneration).IsSuccess.Should().BeTrue();

        await using var verification = database.CreateDbContext();
        (await verification.GroupMembers.SingleAsync(item => item.GroupId == seed.GroupId
            && item.UserId == seed.MemberId)).Status.Should().Be(GroupMemberStatus.Removed);
        (await verification.GroupInvitations.CountAsync(item => item.GroupId == seed.GroupId
            && item.ExpiresAtUtc > new FixedClock().UtcNow)).Should().Be(1);
        (await verification.GroupInvitationOperations.CountAsync()).Should().Be(1);
        (await verification.GroupMemberRemovalOperations.CountAsync()).Should().Be(1);
        (await verification.Notifications.CountAsync()).Should().Be(1);
    }

    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData("join", true)]
    [InlineData("join", false)]
    [InlineData("member-leave", true)]
    [InlineData("member-leave", false)]
    [InlineData("host-leave", true)]
    [InlineData("host-leave", false)]
    public async Task OrderedInterleavings_ProduceTheExpectedFinalState(string otherAction, bool removeFirst)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var code = $"U{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        if (otherAction == "join")
        {
            await using var setup = database.CreateDbContext();
            var now = new FixedClock().UtcNow;
            setup.GroupInvitations.Add(GroupInvitation.Create(
                seed.GroupId, seed.HostId, code, now.AddHours(1), 3, now));
            await setup.SaveChangesAsync();
        }

        async Task<Application.Common.Models.Result<RemoveGroupMemberResponse>> RemoveAsync()
        {
            await using var context = database.CreateDbContext();
            return await CreateHandler(context).Handle(
                new(seed.GroupId, seed.MemberId, seed.HostId, Guid.NewGuid()), CancellationToken.None);
        }

        async Task<(bool Success, string? ErrorCode)> OtherAsync()
        {
            await using var context = database.CreateDbContext();
            if (otherAction == "join")
            {
                var result = await new JoinTravelGroupCommandHandler(
                    context, new FixedClock(), new SqlServerGroupJoinLock(context)).Handle(
                    new(code, seed.MemberId, Guid.NewGuid()), CancellationToken.None);
                return (result.IsSuccess, result.IsFailure ? result.ErrorCode : null);
            }

            var travelerId = otherAction == "host-leave" ? seed.HostId : seed.MemberId;
            var leave = await new LeaveTravelGroupCommandHandler(
                context, new FixedClock(), new SqlServerGroupLeaveLock(context),
                new ItineraryAccessService(context),
                NullLogger<LeaveTravelGroupCommandHandler>.Instance).Handle(
                new(seed.GroupId, travelerId, Guid.NewGuid()), CancellationToken.None);
            return (leave.IsSuccess, leave.IsFailure ? leave.ErrorCode : null);
        }

        Application.Common.Models.Result<RemoveGroupMemberResponse> removal;
        (bool Success, string? ErrorCode) other;
        if (removeFirst)
        {
            removal = await RemoveAsync();
            other = await OtherAsync();
        }
        else
        {
            other = await OtherAsync();
            removal = await RemoveAsync();
        }

        if (otherAction == "join")
        {
            removal.IsSuccess.Should().BeTrue();
            other.Success.Should().Be(removeFirst);
            if (!removeFirst) other.ErrorCode.Should().Be(TravelGroupErrorCodes.AlreadyActiveMember);
        }
        else if (otherAction == "member-leave")
        {
            removal.IsSuccess.Should().Be(removeFirst);
            other.Success.Should().Be(!removeFirst);
            if (removeFirst) other.ErrorCode.Should().Be(TravelGroupErrorCodes.ActiveMembershipRequired);
            else removal.ErrorCode.Should().Be(TravelGroupErrorCodes.MemberNotActive);
        }
        else
        {
            other.Success.Should().BeTrue();
            removal.IsSuccess.Should().Be(removeFirst);
            if (!removeFirst) removal.ErrorCode.Should().Be(TravelGroupErrorCodes.HostPermissionRequired);
        }

        await using var verification = database.CreateDbContext();
        var group = await verification.TravelGroups.SingleAsync(item => item.Id == seed.GroupId);
        var member = await verification.GroupMembers.SingleAsync(item => item.GroupId == seed.GroupId
            && item.UserId == seed.MemberId);
        if (otherAction == "host-leave")
        {
            group.Status.Should().Be(removeFirst ? TravelGroupStatus.Closed : TravelGroupStatus.Active);
            if (!removeFirst) group.HostUserId.Should().Be(seed.MemberId);
        }
        member.Status.Should().Be(otherAction switch
        {
            "join" when removeFirst => GroupMemberStatus.Active,
            "member-leave" when !removeFirst => GroupMemberStatus.Left,
            "host-leave" when !removeFirst => GroupMemberStatus.Active,
            _ => GroupMemberStatus.Removed,
        });
        (await verification.GroupMemberRemovalOperations.CountAsync()).Should().Be(removal.IsSuccess ? 1 : 0);
        (await verification.AuditLogs.CountAsync()).Should().Be(removal.IsSuccess ? 1 : 0);
    }

    private static RemoveGroupMemberCommandHandler CreateHandler(ApplicationDbContext context) =>
        new(context, new FixedClock(), new SqlServerGroupJoinLock(context),
            new ItineraryAccessService(context), NullLogger<RemoveGroupMemberCommandHandler>.Instance);

    private static async Task<(long GroupId, long HostId, long MemberId)> SeedAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var now = new FixedClock().UtcNow;
        var host = new User
        {
            Email = $"uc20-host-{Guid.NewGuid():N}@example.com",
            FullName = "Host",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var member = new User
        {
            Email = $"uc20-member-{Guid.NewGuid():N}@example.com",
            FullName = "Member",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        context.Users.AddRange(host, member);
        await context.SaveChangesAsync();
        var itinerary = Itinerary.CreateManual(host.Id, "Shared", Itinerary.ActiveStatus, now);
        context.Itineraries.Add(itinerary);
        await context.SaveChangesAsync();
        var group = TravelGroup.Create(itinerary.Id, host.Id, "Shared group", now);
        context.TravelGroups.Add(group);
        context.GroupMembers.AddRange(GroupMember.CreateHost(group, host.Id, now),
            GroupMember.CreateMember(group, member.Id, now));
        await context.SaveChangesAsync();
        return (group.Id, host.Id, member.Id);
    }

    private sealed class FixedClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
    }
}