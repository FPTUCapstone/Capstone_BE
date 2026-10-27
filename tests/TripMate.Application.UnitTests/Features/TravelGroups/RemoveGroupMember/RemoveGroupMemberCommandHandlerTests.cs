using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Application.Features.TravelGroups.RemoveGroupMember;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TravelGroups.RemoveGroupMember;

public sealed class RemoveGroupMemberCommandHandlerTests
{
    private readonly FakeDateTimeProvider _clock = new();

    [Fact]
    public async Task HostRemoval_CommitsMembershipNoticeAuditAndReplay()
    {
        await using var db = TestDbContext.Create();
        var (group, host, member) = await SeedAsync(db);
        var handler = CreateHandler(db);
        var command = new RemoveGroupMemberCommand(group.Id, member.UserId, host.UserId, Guid.NewGuid());

        var first = await handler.Handle(command, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        first.Value.MemberName.Should().Be("Target Traveler");
        first.Value.RemovedAtUtc.Should().Be(_clock.UtcNow);
        member.Status.Should().Be(GroupMemberStatus.Removed);
        member.LeftAtUtc.Should().Be(_clock.UtcNow);
        member.LocationSharingEnabled.Should().BeFalse();
        group.HostUserId.Should().Be(host.UserId);
        (await db.GroupMemberRemovalOperations.CountAsync()).Should().Be(1);
        (await db.AuditLogs.CountAsync()).Should().Be(1);
        (await db.Notifications.CountAsync()).Should().Be(1);
        var notice = await db.Notifications.SingleAsync();
        notice.UserId.Should().Be(member.UserId);
        notice.Channel.Should().Be(NotificationChannel.InApp);
        notice.Type.Should().Be(Notification.TravelGroupMemberRemovedType);
        notice.Body.Should().Contain(group.Name);
        notice.Body.Should().NotContain("itinerary");
        var audit = await db.AuditLogs.SingleAsync();
        audit.ActorUserId.Should().Be(host.UserId);
        audit.AffectedEntityId.Should().Be(group.Id);
        audit.AfterData.Should().Contain(member.UserId.ToString());

        member.Reactivate(_clock.UtcNow.AddHours(1));
        await db.SaveChangesAsync();
        var replay = await handler.Handle(command, CancellationToken.None);
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Should().Be(first.Value);
        member.Status.Should().Be(GroupMemberStatus.Active);
        (await db.GroupMemberRemovalOperations.CountAsync()).Should().Be(1);
        (await db.AuditLogs.CountAsync()).Should().Be(1);
        (await db.Notifications.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task KeyReuseWithAnotherTarget_ReturnsConflictWithoutRemovingNewTarget()
    {
        await using var db = TestDbContext.Create();
        var (group, host, member) = await SeedAsync(db);
        var other = GroupMember.CreateMember(group, 30, _clock.UtcNow);
        db.GroupMembers.Add(other);
        await db.SaveChangesAsync();
        var handler = CreateHandler(db);
        var key = Guid.NewGuid();
        (await handler.Handle(new(group.Id, member.UserId, host.UserId, key), CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        var mismatch = await handler.Handle(new(group.Id, other.UserId, host.UserId, key), CancellationToken.None);
        mismatch.ErrorCode.Should().Be(TravelGroupErrorCodes.IdempotencyKeyPayloadMismatch);
        other.Status.Should().Be(GroupMemberStatus.Active);
    }

    [Fact]
    public async Task AmbiguousCommitRetry_ReplaysWithoutDuplicateSideEffects()
    {
        await using var db = TestDbContext.Create();
        var (group, host, member) = await SeedAsync(db);
        db.SerializableTransactionCompletionFailureFactory = attempt => attempt == 1
            ? new SimulatedDeadlockException()
            : null;

        var result = await CreateHandler(db).Handle(
            new(group.Id, member.UserId, host.UserId, Guid.NewGuid()), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        db.TransactionExecutionCount.Should().Be(2);
        (await db.GroupMemberRemovalOperations.CountAsync()).Should().Be(1);
        (await db.AuditLogs.CountAsync()).Should().Be(1);
        (await db.Notifications.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Removal_RevokesOnlyGroupDependentNavigation()
    {
        await using var db = TestDbContext.Create();
        var itinerary = Itinerary.CreateManual(1, "Shared plan", Itinerary.ActiveStatus, _clock.UtcNow);
        db.Itineraries.Add(itinerary);
        await db.SaveChangesAsync();
        var (group, host, member) = await SeedAsync(db);
        var session = TripSession.Start(
            itinerary.Id, itinerary.Id, member.UserId, Guid.NewGuid(),
            _clock.UtcNow.AddHours(-1), _clock.UtcNow.AddHours(2),
            [TripSessionItem.Snapshot(1, 1, 1, "Meeting point", 16m, 108m,
                _clock.UtcNow, _clock.UtcNow.AddHours(1), true)]);
        db.TripSessions.Add(session);
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(
            new(group.Id, member.UserId, host.UserId, Guid.NewGuid()), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        session.FsmState.Should().Be(TripSession.CompletedState);
        session.CompletionReason.Should().Be(TripSession.GroupAccessRevokedReason);
    }

    [Fact]
    public async Task Removal_PreservesNavigationOwnedIndependentlyByTarget()
    {
        await using var db = TestDbContext.Create();
        var itinerary = Itinerary.CreateManual(20, "Personal plan", Itinerary.ActiveStatus, _clock.UtcNow);
        db.Itineraries.Add(itinerary);
        await db.SaveChangesAsync();
        var (group, host, member) = await SeedAsync(db);
        var session = TripSession.Start(
            itinerary.Id, itinerary.Id, member.UserId, Guid.NewGuid(),
            _clock.UtcNow.AddHours(-1), _clock.UtcNow.AddHours(2),
            [TripSessionItem.Snapshot(1, 1, 1, "Meeting point", 16m, 108m,
                _clock.UtcNow, _clock.UtcNow.AddHours(1), true)]);
        db.TripSessions.Add(session);
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(
            new(group.Id, member.UserId, host.UserId, Guid.NewGuid()), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        session.FsmState.Should().Be(TripSession.NavigatingState);
        session.EndedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task NonHostAndInactiveTarget_AreRejectedWithoutSideEffects()
    {
        await using var db = TestDbContext.Create();
        var (group, host, member) = await SeedAsync(db);
        var handler = CreateHandler(db);
        var denied = await handler.Handle(
            new RemoveGroupMemberCommand(group.Id, host.UserId, member.UserId, Guid.NewGuid()),
            CancellationToken.None);
        denied.ErrorCode.Should().Be(TravelGroupErrorCodes.HostPermissionRequired);
        member.Leave(_clock.UtcNow);
        var inactive = await handler.Handle(
            new RemoveGroupMemberCommand(group.Id, member.UserId, host.UserId, Guid.NewGuid()),
            CancellationToken.None);
        inactive.ErrorCode.Should().Be(TravelGroupErrorCodes.MemberNotActive);
        (await db.Notifications.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MissingClosedAndInconsistentGroups_AreRejected()
    {
        await using var db = TestDbContext.Create();
        var (group, host, member) = await SeedAsync(db);
        var handler = CreateHandler(db);
        var missing = await handler.Handle(new(group.Id + 100, member.UserId, host.UserId, Guid.NewGuid()),
            CancellationToken.None);
        missing.ErrorCode.Should().Be(TravelGroupErrorCodes.GroupNotFound);

        host.Leave(_clock.UtcNow);
        var inconsistent = await handler.Handle(new(group.Id, member.UserId, host.UserId, Guid.NewGuid()),
            CancellationToken.None);
        inconsistent.ErrorCode.Should().Be(TravelGroupErrorCodes.MemberListInconsistent);

        member.Leave(_clock.UtcNow);
        group.Close(_clock.UtcNow);
        var closed = await handler.Handle(new(group.Id, member.UserId, host.UserId, Guid.NewGuid()),
            CancellationToken.None);
        closed.ErrorCode.Should().Be(TravelGroupErrorCodes.GroupClosed);
        (await db.GroupMemberRemovalOperations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public void Validator_RejectsSelfRemovalAndInvalidIdentifiers()
    {
        var result = new RemoveGroupMemberCommandValidator().Validate(
            new RemoveGroupMemberCommand(0, 0, 0, Guid.Empty));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(5);
    }

    private async Task<(TravelGroup Group, GroupMember Host, GroupMember Member)> SeedAsync(TestDbContext db)
    {
        var group = TravelGroup.Create(1, 1, "Test group", _clock.UtcNow.AddDays(-1));
        db.TravelGroups.Add(group);
        db.Users.Add(new User { Id = 20, FullName = "Target Traveler", Role = UserRole.Traveler });
        await db.SaveChangesAsync();
        var host = GroupMember.CreateHost(group, 1, _clock.UtcNow.AddDays(-1));
        var member = GroupMember.CreateMember(group, 20, _clock.UtcNow.AddHours(-1));
        db.GroupMembers.AddRange(host, member);
        await db.SaveChangesAsync();
        return (group, host, member);
    }

    private RemoveGroupMemberCommandHandler CreateHandler(TestDbContext db) =>
        new(db, _clock, new NoOpLock(), new ItineraryAccessService(db),
            NullLogger<RemoveGroupMemberCommandHandler>.Instance);

    private sealed class NoOpLock : IGroupJoinLock
    {
        public Task AcquireOperationLockAsync(long travelerUserId, Guid key, CancellationToken token) => Task.CompletedTask;
        public Task AcquireCodeLockAsync(string code, CancellationToken token) => Task.CompletedTask;
        public Task AcquireGroupLockAsync(long groupId, CancellationToken token) => Task.CompletedTask;
    }
}
