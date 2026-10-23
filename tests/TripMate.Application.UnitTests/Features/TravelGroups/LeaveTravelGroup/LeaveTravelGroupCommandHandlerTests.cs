using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Application.Features.TravelGroups.LeaveTravelGroup;
using TripMate.Application.UnitTests.Features.TravelGroups.ManageInvitation;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Constants;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TravelGroups.LeaveTravelGroup;

public sealed class LeaveTravelGroupCommandHandlerTests
{
    private readonly FakeDateTimeProvider _clock = new();

    [Fact]
    public async Task Handle_WhenFinalHostLeaves_ClosesGroupAndExpiresAllInvitations()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db);
        var invitation = GroupInvitation.Create(
            group.Id,
            group.HostUserId,
            "LEAVE001",
            _clock.UtcNow.AddDays(10),
            TravelGroupConstants.UnlimitedInvitationUses,
            _clock.UtcNow);
        db.GroupInvitations.Add(invitation);
        await db.SaveChangesAsync();

        var handler = new LeaveTravelGroupCommandHandler(db, _clock, new RecordingGroupInvitationLock());
        var result = await handler.Handle(
            new LeaveTravelGroupCommand(group.Id, group.HostUserId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        group.ClosedAtUtc.Should().Be(_clock.UtcNow);
        (await db.GroupMembers.SingleAsync()).Status.Should().Be(GroupMemberStatus.Left);
        (await db.GroupMembers.SingleAsync()).LocationSharingEnabled.Should().BeFalse();
        (await db.GroupInvitations.SingleAsync()).ExpiresAtUtc.Should().Be(_clock.UtcNow);
    }

    [Fact]
    public async Task Handle_WhenHostHasOtherActiveMembers_RequiresTransferWithoutChangingMembership()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db);
        var otherUser = new User
        {
            Email = $"member-{Guid.NewGuid():N}@example.com",
            FullName = "Other Member",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
        };
        db.Users.Add(otherUser);
        await db.SaveChangesAsync();
        db.GroupMembers.Add(GroupMember.CreateMember(group, otherUser.Id, _clock.UtcNow));
        await db.SaveChangesAsync();

        var handler = new LeaveTravelGroupCommandHandler(db, _clock, new RecordingGroupInvitationLock());
        var result = await handler.Handle(
            new LeaveTravelGroupCommand(group.Id, group.HostUserId),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelGroupErrorCodes.HostTransferRequired);
        (await db.GroupMembers.SingleAsync(member => member.UserId == group.HostUserId)).Status
            .Should().Be(GroupMemberStatus.Active);
        group.ClosedAtUtc.Should().BeNull();
    }

    private async Task<TravelGroup> AddGroupAsync(TestDbContext db)
    {
        var host = new User
        {
            Email = $"host-{Guid.NewGuid():N}@example.com",
            FullName = "Host",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
        };
        db.Users.Add(host);
        await db.SaveChangesAsync();

        var group = TravelGroup.Create(1, host.Id, "Lifecycle Test Group", _clock.UtcNow);
        var hostMember = GroupMember.CreateHost(group, host.Id, _clock.UtcNow);
        group.AddMember(hostMember);
        db.TravelGroups.Add(group);
        db.GroupMembers.Add(hostMember);
        await db.SaveChangesAsync();
        return group;
    }
}
