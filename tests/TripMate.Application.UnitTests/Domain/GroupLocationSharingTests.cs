using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public sealed class GroupLocationSharingTests
{
    [Fact]
    public void ClearingLocation_InvalidatesEarlierSessionWithoutOptingOut()
    {
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var group = TravelGroup.Create(1, 2, "Group", now);
        var member = GroupMember.CreateHost(group, 2, now);
        member.SetLocationSharing(true, now);
        var previousVersion = member.LocationSharingUpdatedAtUtc;

        member.InvalidateLocationSession(now);

        member.LocationSharingEnabled.Should().BeTrue();
        member.LocationSharingUpdatedAtUtc.Should().BeAfter(previousVersion!.Value);
    }
    [Fact]
    public void Member_CanOptInAndOut_WithoutChangingMembership()
    {
        var now = DateTimeOffset.UtcNow;
        var group = TravelGroup.Create(1, 2, "Group", now);
        var member = GroupMember.CreateHost(group, 2, now);

        member.SetLocationSharing(true, now.AddMinutes(1));
        member.LocationSharingEnabled.Should().BeTrue();
        member.LocationSharingUpdatedAtUtc.Should().Be(now.AddMinutes(1));
        member.Status.Should().Be(GroupMemberStatus.Active);

        member.SetLocationSharing(false, now.AddMinutes(2));
        member.LocationSharingEnabled.Should().BeFalse();
        member.LocationSharingUpdatedAtUtc.Should().Be(now.AddMinutes(2));
    }

    [Theory]
    [InlineData(GroupMemberStatus.Left)]
    [InlineData(GroupMemberStatus.Removed)]
    public void InactiveMember_CannotOptIn(GroupMemberStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        var group = TravelGroup.Create(1, 2, "Group", now);
        var member = GroupMember.CreateHost(group, 2, now);
        typeof(GroupMember).GetProperty(nameof(GroupMember.Status))!.SetValue(member, status);

        var act = () => member.SetLocationSharing(true, now);

        act.Should().Throw<InvalidOperationException>();
        member.LocationSharingEnabled.Should().BeFalse();
    }

    [Fact]
    public void ReactivatedMember_DoesNotInheritOptIn()
    {
        var now = DateTimeOffset.UtcNow;
        var group = TravelGroup.Create(1, 2, "Group", now);
        var member = GroupMember.CreateHost(group, 2, now);
        member.SetLocationSharing(true, now);
        typeof(GroupMember).GetProperty(nameof(GroupMember.Status))!
            .SetValue(member, GroupMemberStatus.Left);

        member.Reactivate(now.AddDays(1));

        member.LocationSharingEnabled.Should().BeFalse();
        member.LocationSharingUpdatedAtUtc.Should().BeNull();
    }
}