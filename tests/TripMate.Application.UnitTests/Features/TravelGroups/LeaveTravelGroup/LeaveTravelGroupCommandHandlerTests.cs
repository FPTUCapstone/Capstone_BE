using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Application.Features.TravelGroups.LeaveTravelGroup;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TravelGroups.LeaveTravelGroup;

public sealed class LeaveTravelGroupCommandHandlerTests
{
    private readonly FakeDateTimeProvider _clock = new();
    private readonly RecordingLeaveLock _lock = new();

    [Fact]
    public async Task Leave_RetriesTheWholeTransactionAfterDeadlock()
    {
        await using var db = TestDbContext.Create();
        var (group, host) = await SeedAsync(db);
        _lock.OperationLockFailureFactory = attempt => attempt == 1
            ? new SimulatedDeadlockException()
            : null;

        var result = await CreateHandler(db).Handle(
            new LeaveTravelGroupCommand(group.Id, host.UserId, Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.TransactionExecutionCount.Should().Be(2);
        _lock.Events.Should().Equal("operation", "operation", "group");
        (await db.GroupLeaveOperations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Leave_RetriesTheWholeTransactionAfterOptimisticConcurrencyFailure()
    {
        await using var db = TestDbContext.Create();
        var (group, host) = await SeedAsync(db);
        _lock.OperationLockFailureFactory = attempt => attempt == 1
            ? new DbUpdateConcurrencyException("Concurrent session mutation")
            : null;

        var result = await CreateHandler(db).Handle(
            new LeaveTravelGroupCommand(group.Id, host.UserId, Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.TransactionExecutionCount.Should().Be(2);
        (await db.GroupLeaveOperations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Leave_AfterThreeDeadlocksReturnsTemporaryFailureWithoutMutation()
    {
        await using var db = TestDbContext.Create();
        var (group, host) = await SeedAsync(db);
        _lock.OperationLockFailureFactory = _ => new SimulatedDeadlockException();

        var result = await CreateHandler(db).Handle(
            new LeaveTravelGroupCommand(group.Id, host.UserId, Guid.NewGuid()), CancellationToken.None);

        result.ErrorCode.Should().Be(TravelGroupErrorCodes.ConcurrentRetryExhausted);
        db.TransactionExecutionCount.Should().Be(3);
        host.Status.Should().Be(GroupMemberStatus.Active);
        (await db.GroupLeaveOperations.CountAsync()).Should().Be(0);
        (await db.Notifications.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Leave_DoesNotRetryUnrelatedErrors()
    {
        await using var db = TestDbContext.Create();
        var (group, host) = await SeedAsync(db);
        _lock.OperationLockFailureFactory = _ => new InvalidOperationException("Unexpected failure");

        var leave = () => CreateHandler(db).Handle(
            new LeaveTravelGroupCommand(group.Id, host.UserId, Guid.NewGuid()), CancellationToken.None);

        await leave.Should().ThrowAsync<InvalidOperationException>();
        db.TransactionExecutionCount.Should().Be(1);
    }

    [Fact]
    public async Task HostLeave_RetryAfterAmbiguousCommitReplaysWithoutDuplicateNotification()
    {
        await using var db = TestDbContext.Create();
        var (group, host) = await SeedAsync(db);
        db.GroupMembers.Add(GroupMember.CreateMember(group, 20, _clock.UtcNow.AddHours(-1)));
        await db.SaveChangesAsync();
        db.SerializableTransactionCompletionFailureFactory = attempt => attempt == 1
            ? new SimulatedDeadlockException()
            : null;

        var result = await CreateHandler(db).Handle(
            new LeaveTravelGroupCommand(group.Id, host.UserId, Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.NewHostUserId.Should().Be(20);
        db.TransactionExecutionCount.Should().Be(2);
        (await db.Notifications.CountAsync()).Should().Be(1);
        (await db.GroupLeaveOperations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task MemberLeave_RevokesOnlyGroupDependentOpenNavigation()
    {
        await using var db = TestDbContext.Create();
        var itinerary = Itinerary.CreateManual(1, "Shared plan", Itinerary.ActiveStatus, _clock.UtcNow);
        db.Itineraries.Add(itinerary);
        await db.SaveChangesAsync();
        var (group, _) = await SeedAsync(db);
        group.ItineraryId.Should().Be(itinerary.Id);
        var member = GroupMember.CreateMember(group, 20, _clock.UtcNow.AddHours(-1));
        db.GroupMembers.Add(member);
        var session = TripSession.Start(
            itinerary.Id, itinerary.Id, member.UserId, Guid.NewGuid(),
            _clock.UtcNow.AddHours(-1), _clock.UtcNow.AddHours(2),
            [TripSessionItem.Snapshot(1, 1, 1, "Meeting point", 16m, 108m,
                _clock.UtcNow, _clock.UtcNow.AddHours(1), true)]);
        db.TripSessions.Add(session);
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(
            new LeaveTravelGroupCommand(group.Id, member.UserId, Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        session.FsmState.Should().Be(TripSession.CompletedState);
        session.CompletionReason.Should().Be(TripSession.GroupAccessRevokedReason);
        session.StateHistory.Last().TriggeredBy.Should().Be(TripStateHistory.TriggeredBySystem);
    }

    [Fact]
    public async Task LastHostLeave_PreservesIndependentlyOwnedNavigation()
    {
        await using var db = TestDbContext.Create();
        var itinerary = Itinerary.CreateManual(1, "Personal plan", Itinerary.ActiveStatus, _clock.UtcNow);
        db.Itineraries.Add(itinerary);
        await db.SaveChangesAsync();
        var (group, host) = await SeedAsync(db);
        group.ItineraryId.Should().Be(itinerary.Id);
        var session = TripSession.Start(
            itinerary.Id, itinerary.Id, host.UserId, Guid.NewGuid(),
            _clock.UtcNow.AddHours(-1), _clock.UtcNow.AddHours(2),
            [TripSessionItem.Snapshot(1, 1, 1, "Meeting point", 16m, 108m,
                _clock.UtcNow, _clock.UtcNow.AddHours(1), true)]);
        db.TripSessions.Add(session);
        await db.SaveChangesAsync();

        var result = await CreateHandler(db).Handle(
            new LeaveTravelGroupCommand(group.Id, host.UserId, Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        session.FsmState.Should().Be(TripSession.NavigatingState);
        session.EndedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task MemberLeave_PreservesHostAndGroup_AndReplaysOriginalOutcome()
    {
        await using var db = TestDbContext.Create();
        var (group, host) = await SeedAsync(db);
        var member = GroupMember.CreateMember(group, 20, _clock.UtcNow.AddHours(-1));
        db.GroupMembers.Add(member);
        await db.SaveChangesAsync();

        var handler = CreateHandler(db);
        var key = Guid.NewGuid();
        var first = await handler.Handle(new LeaveTravelGroupCommand(group.Id, member.UserId, key), CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        first.Value.GroupId.Should().Be(group.Id);
        first.Value.MembershipStatus.Should().Be(nameof(GroupMemberStatus.Left));
        first.Value.GroupClosed.Should().BeFalse();
        first.Value.NewHostUserId.Should().BeNull();
        first.Value.LeftAtUtc.Should().Be(_clock.UtcNow);
        member.Status.Should().Be(GroupMemberStatus.Left);
        member.LeftAtUtc.Should().Be(_clock.UtcNow);
        group.HostUserId.Should().Be(host.UserId);
        group.Status.Should().Be(TravelGroupStatus.Active);
        (await db.Notifications.CountAsync()).Should().Be(0);
        db.TransactionExecutionCount.Should().Be(1);
        _lock.Events.Should().Equal("operation", "group");

        member.Reactivate(_clock.UtcNow.AddDays(1));
        await db.SaveChangesAsync();
        var replay = await handler.Handle(new LeaveTravelGroupCommand(group.Id, member.UserId, key), CancellationToken.None);
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Should().Be(first.Value);
        member.Status.Should().Be(GroupMemberStatus.Active);
        (await db.GroupLeaveOperations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task HostLeave_SelectsEarliestJoinedThenSmallestUserId()
    {
        await using var db = TestDbContext.Create();
        var (group, host) = await SeedAsync(db);
        var sameTime = _clock.UtcNow.AddHours(-2);
        db.GroupMembers.Add(GroupMember.CreateMember(group, 30, sameTime));
        db.GroupMembers.Add(GroupMember.CreateMember(group, 20, sameTime));
        db.GroupMembers.Add(GroupMember.CreateMember(group, 10, _clock.UtcNow.AddHours(-1)));
        await db.SaveChangesAsync();

        var key = Guid.NewGuid();
        var result = await CreateHandler(db)
            .Handle(new LeaveTravelGroupCommand(group.Id, host.UserId, key), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.NewHostUserId.Should().Be(20);
        group.HostUserId.Should().Be(20);
        host.Status.Should().Be(GroupMemberStatus.Left);
        var notice = (await db.Notifications.ToListAsync()).Should().ContainSingle().Subject;
        notice.UserId.Should().Be(20);
        notice.Channel.Should().Be(NotificationChannel.InApp);
        notice.Type.Should().Be(Notification.TravelGroupHostTransferredType);
        notice.RelatedEntityType.Should().Be(Notification.TravelGroupRelatedEntityType);
        notice.RelatedEntityId.Should().Be(group.Id);
        notice.Status.Should().Be(NotificationStatus.Pending);
        notice.CreatedAtUtc.Should().Be(_clock.UtcNow);

        var replay = await CreateHandler(db).Handle(
            new LeaveTravelGroupCommand(group.Id, host.UserId, key), CancellationToken.None);
        replay.IsSuccess.Should().BeTrue();
        (await db.Notifications.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task LastHostLeave_ClosesGroupAndExpiresInvitations()
    {
        await using var db = TestDbContext.Create();
        var (group, host) = await SeedAsync(db);
        var invitation = GroupInvitation.Create(group.Id, host.UserId, "ABCDEFGH", _clock.UtcNow.AddDays(2), 20, _clock.UtcNow.AddDays(-1));
        db.GroupInvitations.Add(invitation);
        await db.SaveChangesAsync();

        var result = await CreateHandler(db)
            .Handle(new LeaveTravelGroupCommand(group.Id, host.UserId, Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.GroupClosed.Should().BeTrue();
        group.Status.Should().Be(TravelGroupStatus.Closed);
        group.ClosedAtUtc.Should().Be(_clock.UtcNow);
        group.HostUserId.Should().Be(host.UserId);
        invitation.IsUsableAt(_clock.UtcNow).Should().BeFalse();
        (await db.Notifications.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task InvalidMembershipAndKeyReuse_ReturnSpecifiedFailures()
    {
        await using var db = TestDbContext.Create();
        var (group, host) = await SeedAsync(db);
        var handler = CreateHandler(db);
        var absent = await handler.Handle(new LeaveTravelGroupCommand(group.Id, 999, Guid.NewGuid()), CancellationToken.None);
        absent.ErrorCode.Should().Be(TravelGroupErrorCodes.ActiveMembershipRequired);

        var key = Guid.NewGuid();
        (await handler.Handle(new LeaveTravelGroupCommand(group.Id, host.UserId, key), CancellationToken.None)).IsSuccess.Should().BeTrue();
        var alreadyLeft = await handler.Handle(new LeaveTravelGroupCommand(group.Id, host.UserId, Guid.NewGuid()), CancellationToken.None);
        alreadyLeft.ErrorCode.Should().Be(TravelGroupErrorCodes.GroupClosed);
        var mismatch = await handler.Handle(new LeaveTravelGroupCommand(group.Id + 1, host.UserId, key), CancellationToken.None);
        mismatch.ErrorCode.Should().Be(TravelGroupErrorCodes.IdempotencyKeyPayloadMismatch);
    }

    [Fact]
    public async Task AlreadyLeftInOpenGroup_WithNewKey_ReturnsConflictWithoutNewOperation()
    {
        await using var db = TestDbContext.Create();
        var (group, _) = await SeedAsync(db);
        var member = GroupMember.CreateMember(group, 20, _clock.UtcNow.AddHours(-1));
        member.Leave(_clock.UtcNow);
        db.GroupMembers.Add(member);
        await db.SaveChangesAsync();

        var result = await CreateHandler(db)
            .Handle(new LeaveTravelGroupCommand(group.Id, member.UserId, Guid.NewGuid()), CancellationToken.None);

        result.ErrorCode.Should().Be(TravelGroupErrorCodes.AlreadyLeft);
        (await db.GroupLeaveOperations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MissingActiveHost_ReturnsServerInvariantErrorWithoutMutation()
    {
        await using var db = TestDbContext.Create();
        var (group, host) = await SeedAsync(db);
        var member = GroupMember.CreateMember(group, 20, _clock.UtcNow.AddHours(-1));
        db.GroupMembers.Add(member);
        host.Leave(_clock.UtcNow.AddMinutes(-1));
        await db.SaveChangesAsync();

        var result = await CreateHandler(db)
            .Handle(new LeaveTravelGroupCommand(group.Id, member.UserId, Guid.NewGuid()), CancellationToken.None);

        result.ErrorCode.Should().Be(TravelGroupErrorCodes.MemberListInconsistent);
        member.Status.Should().Be(GroupMemberStatus.Active);
        (await db.GroupLeaveOperations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MissingGroup_ReturnsNotFound()
    {
        await using var db = TestDbContext.Create();
        var result = await CreateHandler(db)
            .Handle(new LeaveTravelGroupCommand(999, 1, Guid.NewGuid()), CancellationToken.None);
        result.ErrorCode.Should().Be(TravelGroupErrorCodes.GroupNotFound);
    }

    [Fact]
    public void Validator_RejectsInvalidIdentifiersAndEmptyKey()
    {
        var result = new LeaveTravelGroupCommandValidator().Validate(
            new LeaveTravelGroupCommand(0, 0, Guid.Empty));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(3);
    }

    private async Task<(TravelGroup Group, GroupMember Host)> SeedAsync(TestDbContext db)
    {
        var group = TravelGroup.Create(1, 1, "Test group", _clock.UtcNow.AddDays(-4));
        db.TravelGroups.Add(group);
        await db.SaveChangesAsync();
        var host = GroupMember.CreateHost(group, 1, _clock.UtcNow.AddDays(-4));
        db.GroupMembers.Add(host);
        await db.SaveChangesAsync();
        return (group, host);
    }

    private LeaveTravelGroupCommandHandler CreateHandler(TestDbContext db) =>
        new(db, _clock, _lock, new ItineraryAccessService(db), NullLogger<LeaveTravelGroupCommandHandler>.Instance);

    private sealed class RecordingLeaveLock : IGroupLeaveLock
    {
        public List<string> Events { get; } = [];
        public Func<int, Exception?>? OperationLockFailureFactory { get; set; }
        private int _operationAttempts;

        public Task AcquireOperationLockAsync(long travelerUserId, Guid key, CancellationToken cancellationToken)
        {
            Events.Add("operation");
            if (OperationLockFailureFactory?.Invoke(++_operationAttempts) is { } failure)
            {
                throw failure;
            }
            return Task.CompletedTask;
        }

        public Task AcquireGroupLockAsync(long groupId, CancellationToken cancellationToken)
        {
            Events.Add("group");
            return Task.CompletedTask;
        }
    }
}