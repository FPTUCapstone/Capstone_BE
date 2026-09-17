using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Application.Features.TravelGroups.JoinTravelGroup;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Constants;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TravelGroups.JoinTravelGroup;

public sealed class JoinTravelGroupCommandHandlerTests
{
    private readonly FakeDateTimeProvider _clock = new();
    private readonly FakeGroupJoinLock _lock = new();

    private JoinTravelGroupCommandHandler CreateHandler(TestDbContext db) =>
        new(db, _clock, _lock);

    [Fact]
    public async Task Join_WhenInvitationNotFound_ReturnsInvitationUnavailable()
    {
        await using var db = TestDbContext.Create();
        var handler = CreateHandler(db);

        var result = await handler.Handle(
            new JoinTravelGroupCommand("NOTFOUND", 10, Guid.NewGuid()),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelGroupErrorCodes.InvitationUnavailable);
    }

    [Fact]
    public async Task Join_WhenInvitationExpired_ReturnsInvitationUnavailable()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db);
        var invitation = GroupInvitation.Create(
            group.Id,
            group.HostUserId,
            "EXPIRED1",
            _clock.UtcNow.AddMinutes(-5),
            TravelGroupConstants.UnlimitedInvitationUses,
            _clock.UtcNow.AddDays(-1));
        db.GroupInvitations.Add(invitation);
        await db.SaveChangesAsync();

        var handler = CreateHandler(db);
        var result = await handler.Handle(
            new JoinTravelGroupCommand("EXPIRED1", 10, Guid.NewGuid()),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelGroupErrorCodes.InvitationUnavailable);
    }

    [Fact]
    public async Task Join_WhenInvitationUsageExhausted_ReturnsInvitationUnavailable()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db);
        var invitation = GroupInvitation.Create(
            group.Id,
            group.HostUserId,
            "MAXUSED1",
            _clock.UtcNow.AddDays(5),
            1,
            _clock.UtcNow);
        invitation.IncrementUsedCount(); // UsedCount reaches MaxUses
        db.GroupInvitations.Add(invitation);
        await db.SaveChangesAsync();

        var handler = CreateHandler(db);
        var result = await handler.Handle(
            new JoinTravelGroupCommand("MAXUSED1", 10, Guid.NewGuid()),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelGroupErrorCodes.InvitationUnavailable);
    }

    [Fact]
    public async Task Join_WhenCallerIsAlreadyActiveMember_ReturnsAlreadyActiveMember()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db);
        var invitation = GroupInvitation.Create(
            group.Id,
            group.HostUserId,
            "VALID001",
            _clock.UtcNow.AddDays(5),
            TravelGroupConstants.UnlimitedInvitationUses,
            _clock.UtcNow);
        db.GroupInvitations.Add(invitation);

        // Host is already active member
        var hostMember = GroupMember.CreateHost(group, group.HostUserId, _clock.UtcNow);
        db.GroupMembers.Add(hostMember);
        await db.SaveChangesAsync();

        var handler = CreateHandler(db);
        var result = await handler.Handle(
            new JoinTravelGroupCommand("VALID001", group.HostUserId, Guid.NewGuid()),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelGroupErrorCodes.AlreadyActiveMember);
        (await db.GroupMembers.CountAsync()).Should().Be(1);
        invitation.UsedCount.Should().Be(0);
    }

    [Fact]
    public async Task Join_WhenCallerWasRemoved_ReturnsInvitationUnavailableWithoutRevealingRemoval()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db);
        var invitation = GroupInvitation.Create(
            group.Id,
            group.HostUserId,
            "VALID001",
            _clock.UtcNow.AddDays(5),
            TravelGroupConstants.UnlimitedInvitationUses,
            _clock.UtcNow);
        db.GroupInvitations.Add(invitation);

        var travelerId = 99L;
        var member = GroupMember.CreateMember(group, travelerId, _clock.UtcNow);
        // Set member status to Removed via reflection/field since it represents a removed user
        typeof(GroupMember).GetProperty(nameof(GroupMember.Status))!
            .SetValue(member, GroupMemberStatus.Removed);
        db.GroupMembers.Add(member);
        await db.SaveChangesAsync();

        var handler = CreateHandler(db);
        var result = await handler.Handle(
            new JoinTravelGroupCommand("VALID001", travelerId, Guid.NewGuid()),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelGroupErrorCodes.InvitationUnavailable);
        invitation.UsedCount.Should().Be(0);
    }

    [Fact]
    public async Task Join_WhenCallerPreviouslyLeft_ReactivatesMembershipAndResetsLocationSharing()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db);
        var invitation = GroupInvitation.Create(
            group.Id,
            group.HostUserId,
            "VALID001",
            _clock.UtcNow.AddDays(5),
            TravelGroupConstants.UnlimitedInvitationUses,
            _clock.UtcNow);
        db.GroupInvitations.Add(invitation);

        var travelerId = 99L;
        var member = GroupMember.CreateMember(group, travelerId, _clock.UtcNow.AddDays(-10));
        typeof(GroupMember).GetProperty(nameof(GroupMember.Status))!
            .SetValue(member, GroupMemberStatus.Left);
        typeof(GroupMember).GetProperty(nameof(GroupMember.LeftAtUtc))!
            .SetValue(member, _clock.UtcNow.AddDays(-2));
        typeof(GroupMember).GetProperty(nameof(GroupMember.LocationSharingEnabled))!
            .SetValue(member, true);
        db.GroupMembers.Add(member);
        await db.SaveChangesAsync();

        var rejoinTime = _clock.UtcNow;
        var handler = CreateHandler(db);
        var result = await handler.Handle(
            new JoinTravelGroupCommand("valid001", travelerId, Guid.NewGuid()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.GroupId.Should().Be(group.Id);
        result.Value.GroupName.Should().Be(group.GroupName);
        result.Value.ItineraryId.Should().Be(group.ItineraryId);

        var updatedMember = await db.GroupMembers.FirstAsync(m => m.GroupId == group.Id && m.UserId == travelerId);
        updatedMember.Status.Should().Be(GroupMemberStatus.Active);
        updatedMember.JoinedAtUtc.Should().Be(rejoinTime);
        updatedMember.LeftAtUtc.Should().BeNull();
        updatedMember.LocationSharingEnabled.Should().BeFalse();
        invitation.UsedCount.Should().Be(1);
    }

    [Fact]
    public async Task Join_WhenValidNewMember_CreatesActiveMemberAndOperationRecord()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db);
        var invitation = GroupInvitation.Create(
            group.Id,
            group.HostUserId,
            "VALID001",
            _clock.UtcNow.AddDays(5),
            TravelGroupConstants.UnlimitedInvitationUses,
            _clock.UtcNow);
        db.GroupInvitations.Add(invitation);
        await db.SaveChangesAsync();

        var travelerId = 101L;
        var idempotencyKey = Guid.NewGuid();
        var handler = CreateHandler(db);

        var result = await handler.Handle(
            new JoinTravelGroupCommand("  valid001  ", travelerId, idempotencyKey),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.GroupId.Should().Be(group.Id);
        result.Value.GroupName.Should().Be(group.GroupName);
        result.Value.ItineraryId.Should().Be(group.ItineraryId);

        var member = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == group.Id && m.UserId == travelerId);
        member.Should().NotBeNull();
        member!.Status.Should().Be(GroupMemberStatus.Active);
        member.LocationSharingEnabled.Should().BeFalse();
        invitation.UsedCount.Should().Be(1);

        var operation = await db.GroupJoinOperations.FirstOrDefaultAsync(
            op => op.TravelerUserId == travelerId && op.IdempotencyKey == idempotencyKey);
        operation.Should().NotBeNull();
        operation!.InvitationCode.Should().Be("VALID001");
        operation.GroupId.Should().Be(group.Id);
    }

    [Fact]
    public async Task Join_WhenIdempotencyKeyReplayedWithSameCode_ReturnsCachedGroupWithoutIncrementingAgain()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db);
        var invitation = GroupInvitation.Create(
            group.Id,
            group.HostUserId,
            "VALID001",
            _clock.UtcNow.AddDays(5),
            TravelGroupConstants.UnlimitedInvitationUses,
            _clock.UtcNow);
        db.GroupInvitations.Add(invitation);
        await db.SaveChangesAsync();

        var travelerId = 101L;
        var idempotencyKey = Guid.NewGuid();
        var handler = CreateHandler(db);

        var firstResult = await handler.Handle(
            new JoinTravelGroupCommand("VALID001", travelerId, idempotencyKey),
            CancellationToken.None);
        firstResult.IsSuccess.Should().BeTrue();

        // Expire the invitation to prove replay works even after expiration
        invitation.Expire(_clock.UtcNow.AddHours(-1));
        await db.SaveChangesAsync();

        var replayResult = await handler.Handle(
            new JoinTravelGroupCommand("valid001", travelerId, idempotencyKey),
            CancellationToken.None);

        replayResult.IsSuccess.Should().BeTrue();
        replayResult.Value.GroupId.Should().Be(group.Id);
        (await db.GroupMembers.CountAsync(m => m.GroupId == group.Id && m.UserId == travelerId)).Should().Be(1);
        invitation.UsedCount.Should().Be(1);
    }

    [Fact]
    public async Task Join_WhenIdempotencyKeyReusedWithDifferentCode_ReturnsPayloadMismatch()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db);
        var invitation = GroupInvitation.Create(
            group.Id,
            group.HostUserId,
            "VALID001",
            _clock.UtcNow.AddDays(5),
            TravelGroupConstants.UnlimitedInvitationUses,
            _clock.UtcNow);
        db.GroupInvitations.Add(invitation);
        await db.SaveChangesAsync();

        var travelerId = 101L;
        var idempotencyKey = Guid.NewGuid();
        var handler = CreateHandler(db);

        var firstResult = await handler.Handle(
            new JoinTravelGroupCommand("VALID001", travelerId, idempotencyKey),
            CancellationToken.None);
        firstResult.IsSuccess.Should().BeTrue();

        var mismatchResult = await handler.Handle(
            new JoinTravelGroupCommand("DIFFERNT", travelerId, idempotencyKey),
            CancellationToken.None);

        mismatchResult.IsFailure.Should().BeTrue();
        mismatchResult.ErrorCode.Should().Be(TravelGroupErrorCodes.IdempotencyKeyPayloadMismatch);
    }

    [Fact]
    public async Task Join_AcquiresLocksInStrictOrder()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db);
        var invitation = GroupInvitation.Create(
            group.Id,
            group.HostUserId,
            "ORDER001",
            _clock.UtcNow.AddDays(5),
            TravelGroupConstants.UnlimitedInvitationUses,
            _clock.UtcNow);
        db.GroupInvitations.Add(invitation);
        await db.SaveChangesAsync();

        var travelerId = 105L;
        var idempotencyKey = Guid.NewGuid();
        var handler = CreateHandler(db);

        _lock.AcquiredLocks.Clear();
        await handler.Handle(
            new JoinTravelGroupCommand("ORDER001", travelerId, idempotencyKey),
            CancellationToken.None);

        _lock.AcquiredLocks.Should().HaveCount(3);
        _lock.AcquiredLocks[0].Should().Be($"Operation:{travelerId}:{idempotencyKey:N}");
        _lock.AcquiredLocks[1].Should().Be("Code:ORDER001");
        _lock.AcquiredLocks[2].Should().Be($"Group:{group.Id}");
    }

    private async Task<TravelGroup> AddGroupAsync(TestDbContext db)
    {
        var host = new User
        {
            Email = $"host-{Guid.NewGuid():N}@example.com",
            FullName = "Join Test Host",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
        };
        db.Users.Add(host);
        await db.SaveChangesAsync();

        var group = TravelGroup.Create(1, host.Id, "Join Test Group", _clock.UtcNow);
        db.TravelGroups.Add(group);
        await db.SaveChangesAsync();
        return group;
    }
}