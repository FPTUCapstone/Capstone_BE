using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TravelGroups.ManageInvitation;
using TripMate.Domain.Constants;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class GroupInvitationSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentRequestsWithSameKey_CreateOneInvitationAndReplaySameResult()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var key = Guid.NewGuid();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> GetOrCreateAsync()
        {
            await startGate.Task;
            await using var context = database.CreateDbContext();
            var handler = new GetOrCreateGroupInvitationCommandHandler(
                context,
                new FixedDateTimeProvider(),
                new SqlServerGroupInvitationLock(context),
                new RandomGroupInvitationCodeGenerator());
            var result = await handler.Handle(
                new GetOrCreateGroupInvitationCommand(seed.GroupId, seed.HostUserId, key),
                CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            return result.Value.InviteCode;
        }

        var first = GetOrCreateAsync();
        var second = GetOrCreateAsync();
        startGate.SetResult();
        var inviteCodes = await Task.WhenAll(first, second);

        inviteCodes[0].Should().Be(inviteCodes[1]);
        await using var verification = database.CreateDbContext();
        (await verification.GroupInvitations.CountAsync()).Should().Be(1);
        (await verification.GroupInvitationOperations.CountAsync()).Should().Be(1);
        (await verification.GroupMembers.CountAsync()).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task GetOrCreate_WhenLegacyDataHasSeveralUsableInvitations_ExpiresDuplicates()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var now = new FixedDateTimeProvider().UtcNow;
        await using (var setup = database.CreateDbContext())
        {
            setup.GroupInvitations.AddRange(
                GroupInvitation.Create(
                    seed.GroupId,
                    seed.HostUserId,
                    "OLDER001",
                    now.AddDays(10),
                    int.MaxValue,
                    now.AddMinutes(-1)),
                GroupInvitation.Create(
                    seed.GroupId,
                    seed.HostUserId,
                    "NEWER002",
                    now.AddDays(10),
                    int.MaxValue,
                    now));
            await setup.SaveChangesAsync();
        }

        await using (var context = database.CreateDbContext())
        {
            var handler = new GetOrCreateGroupInvitationCommandHandler(
                context,
                new FixedDateTimeProvider(),
                new SqlServerGroupInvitationLock(context),
                new RandomGroupInvitationCodeGenerator());
            var result = await handler.Handle(
                new GetOrCreateGroupInvitationCommand(seed.GroupId, seed.HostUserId, Guid.NewGuid()),
                CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            result.Value.InviteCode.Should().Be("NEWER002");
        }

        await using var verification = database.CreateDbContext();
        var usableCount = await verification.GroupInvitations.CountAsync(invitation =>
            invitation.GroupId == seed.GroupId
            && invitation.ExpiresAtUtc > now
            && invitation.UsedCount < invitation.MaxUses);
        usableCount.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentRegenerateRequests_LeaveExactlyOneUsableInvitation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task RegenerateAsync()
        {
            await startGate.Task;
            await using var context = database.CreateDbContext();
            var handler = new RegenerateGroupInvitationCommandHandler(
                context,
                new FixedDateTimeProvider(),
                new SqlServerGroupInvitationLock(context),
                new RandomGroupInvitationCodeGenerator());
            var result = await handler.Handle(
                new RegenerateGroupInvitationCommand(seed.GroupId, seed.HostUserId, Guid.NewGuid()),
                CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
        }

        var first = RegenerateAsync();
        var second = RegenerateAsync();
        startGate.SetResult();
        await Task.WhenAll(first, second);

        var now = new FixedDateTimeProvider().UtcNow;
        await using var verification = database.CreateDbContext();
        var usableCount = await verification.GroupInvitations.CountAsync(invitation =>
            invitation.GroupId == seed.GroupId
            && invitation.ExpiresAtUtc > now
            && invitation.UsedCount < invitation.MaxUses);
        usableCount.Should().Be(1);
        (await verification.GroupInvitationOperations.CountAsync()).Should().Be(2);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ReplayedKeyFromAnotherContext_ReturnsOriginalAndRejectsDifferentOperation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var key = Guid.NewGuid();

        await using (var firstContext = database.CreateDbContext())
        {
            var firstHandler = new GetOrCreateGroupInvitationCommandHandler(
                firstContext,
                new FixedDateTimeProvider(),
                new SqlServerGroupInvitationLock(firstContext),
                new RandomGroupInvitationCodeGenerator());
            var first = await firstHandler.Handle(
                new GetOrCreateGroupInvitationCommand(seed.GroupId, seed.HostUserId, key),
                CancellationToken.None);
            first.IsSuccess.Should().BeTrue();
        }

        await using (var replayContext = database.CreateDbContext())
        {
            var replayHandler = new GetOrCreateGroupInvitationCommandHandler(
                replayContext,
                new FixedDateTimeProvider(),
                new SqlServerGroupInvitationLock(replayContext),
                new RandomGroupInvitationCodeGenerator());
            var replay = await replayHandler.Handle(
                new GetOrCreateGroupInvitationCommand(seed.GroupId, seed.HostUserId, key),
                CancellationToken.None);
            replay.IsSuccess.Should().BeTrue();
        }

        await using (var mismatchContext = database.CreateDbContext())
        {
            var mismatchHandler = new RegenerateGroupInvitationCommandHandler(
                mismatchContext,
                new FixedDateTimeProvider(),
                new SqlServerGroupInvitationLock(mismatchContext),
                new RandomGroupInvitationCodeGenerator());
            var mismatch = await mismatchHandler.Handle(
                new RegenerateGroupInvitationCommand(seed.GroupId, seed.HostUserId, key),
                CancellationToken.None);
            mismatch.IsFailure.Should().BeTrue();
            mismatch.ErrorCode.Should().Be("travel_group.idempotency_key_payload_mismatch");
        }

        await using var verification = database.CreateDbContext();
        (await verification.GroupInvitations.CountAsync()).Should().Be(1);
        (await verification.GroupInvitationOperations.CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentRequestsForDifferentGroups_WhenFirstCodeCollides_RetryWithoutServerFailure()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var firstGroup = await SeedAsync(database);
        var secondGroupId = await AddGroupAsync(database, firstGroup.HostUserId, "Second SQL Invitation Group");
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> GetOrCreateAsync(long groupId, params string[] codes)
        {
            await startGate.Task;
            await using var context = database.CreateDbContext();
            var handler = new GetOrCreateGroupInvitationCommandHandler(
                context,
                new FixedDateTimeProvider(),
                new SqlServerGroupInvitationLock(context),
                new SequenceCodeGenerator(codes));
            var result = await handler.Handle(
                new GetOrCreateGroupInvitationCommand(groupId, firstGroup.HostUserId, Guid.NewGuid()),
                CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            return result.Value.InviteCode;
        }

        var first = GetOrCreateAsync(firstGroup.GroupId, "RACE0001", "RACE0002");
        var second = GetOrCreateAsync(secondGroupId, "RACE0001", "RACE0003");
        startGate.SetResult();
        var inviteCodes = await Task.WhenAll(first, second);

        inviteCodes.Should().OnlyHaveUniqueItems();
        await using var verification = database.CreateDbContext();
        (await verification.GroupInvitations.CountAsync()).Should().Be(2);
        (await verification.GroupInvitationOperations.CountAsync()).Should().Be(2);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Regenerate_WhenOperationPersistenceFails_RollsBackInvitationChanges()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var now = new FixedDateTimeProvider().UtcNow;

        await using (var setup = database.CreateDbContext())
        {
            var existingInvitation = GroupInvitation.Create(
                seed.GroupId,
                seed.HostUserId,
                "ROLLBACK",
                now.AddDays(TravelGroupConstants.InvitationCodeExpiryDays),
                TravelGroupConstants.UnlimitedInvitationUses,
                now);
            setup.GroupInvitations.Add(existingInvitation);
            await setup.SaveChangesAsync();

            setup.GroupInvitationOperations.Add(GroupInvitationOperation.Create(
                seed.HostUserId,
                seed.GroupId,
                GroupInvitationOperationType.GetOrCreate,
                Guid.NewGuid(),
                existingInvitation,
                now));
            await setup.SaveChangesAsync();
        }

        await database.ExecuteNonQueryAsync("""
            CREATE UNIQUE INDEX UX_Test_GroupInvitationOperations_GroupId
            ON social.GroupInvitationOperations(group_id);
            """);

        await using (var context = database.CreateDbContext())
        {
            var handler = new RegenerateGroupInvitationCommandHandler(
                context,
                new FixedDateTimeProvider(),
                new SqlServerGroupInvitationLock(context),
                new SequenceCodeGenerator("NEWROLL1"));

            var act = () => handler.Handle(
                new RegenerateGroupInvitationCommand(seed.GroupId, seed.HostUserId, Guid.NewGuid()),
                CancellationToken.None);

            await act.Should().ThrowAsync<DbUpdateException>();
        }

        await using var verification = database.CreateDbContext();
        var invitations = await verification.GroupInvitations
            .Where(invitation => invitation.GroupId == seed.GroupId)
            .ToListAsync();
        invitations.Should().ContainSingle();
        invitations[0].InviteCode.Should().Be("ROLLBACK");
        invitations[0].ExpiresAtUtc.Should().BeAfter(now);
        (await verification.GroupInvitationOperations.CountAsync()).Should().Be(1);
    }

    private static async Task<(long HostUserId, long GroupId)> SeedAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var now = new FixedDateTimeProvider().UtcNow;
        var host = new User
        {
            Email = "sql-invitation-host@example.com",
            FullName = "SQL Invitation Host",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.Users.Add(host);
        await context.SaveChangesAsync();

        var itinerary = Itinerary.CreateManual(host.Id, "SQL Invitation Trip", Itinerary.ActiveStatus, now);
        context.Itineraries.Add(itinerary);
        await context.SaveChangesAsync();

        var group = TravelGroup.Create(itinerary.Id, host.Id, "SQL Invitation Group", now);
        context.TravelGroups.Add(group);
        await context.SaveChangesAsync();
        return (host.Id, group.Id);
    }

    private static async Task<long> AddGroupAsync(
        SqlServerTestDatabase database,
        long hostUserId,
        string groupName)
    {
        await using var context = database.CreateDbContext();
        var now = new FixedDateTimeProvider().UtcNow;
        var itinerary = Itinerary.CreateManual(hostUserId, $"{groupName} itinerary", Itinerary.ActiveStatus, now);
        context.Itineraries.Add(itinerary);
        await context.SaveChangesAsync();

        var group = TravelGroup.Create(itinerary.Id, hostUserId, groupName, now);
        context.TravelGroups.Add(group);
        await context.SaveChangesAsync();
        return group.Id;
    }

    private sealed class FixedDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
    }

    private sealed class SequenceCodeGenerator(params string[] codes) : IGroupInvitationCodeGenerator
    {
        private readonly Queue<string> _codes = new(codes);

        public string Generate() => _codes.Dequeue();
    }
}