using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public sealed class GroupMemberRemovalTests
{
    [Fact]
    public void ActiveMember_CanBeRemovedWithAnEndTime()
    {
        var joinedAt = new DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero);
        var removedAt = joinedAt.AddHours(1);
        var group = TravelGroup.Create(1, 10, "Shared trip", joinedAt);
        var member = GroupMember.CreateMember(group, 20, joinedAt);

        member.Remove(removedAt);

        member.Status.Should().Be(GroupMemberStatus.Removed);
        member.LeftAtUtc.Should().Be(removedAt);
        member.LocationSharingEnabled.Should().BeFalse();
    }

    [Fact]
    public void InactiveMember_CannotBeRemovedAgain()
    {
        var now = new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
        var group = TravelGroup.Create(1, 10, "Shared trip", now);
        var member = GroupMember.CreateMember(group, 20, now.AddHours(-1));
        member.Leave(now);

        var act = () => member.Remove(now.AddMinutes(1));

        act.Should().Throw<InvalidOperationException>();
        member.Status.Should().Be(GroupMemberStatus.Left);
        member.LeftAtUtc.Should().Be(now);
    }
}