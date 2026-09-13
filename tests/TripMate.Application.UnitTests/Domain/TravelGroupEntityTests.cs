using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Domain;

public sealed class TravelGroupEntityTests
{
    [Fact]
    public void Create_TrimsNameAndCreatesHostMemberWithMatchingHost()
    {
        var now = DateTimeOffset.UtcNow;
        var group = TravelGroup.Create(10, 20, "  Summer trip  ", now);
        var member = GroupMember.CreateHost(group, 20, now);
        group.AddMember(member);
        var request = TravelGroupCreationRequest.Create(
            20,
            Guid.NewGuid(),
            10,
            group.Name,
            group,
            now);

        group.Name.Should().Be("Summer trip");
        member.Status.Should().Be(GroupMemberStatus.Active);
        member.LocationSharingEnabled.Should().BeFalse();
        member.TravelGroup.Should().BeSameAs(group);
        group.GroupMembers.Should().ContainSingle().Which.Should().BeSameAs(member);
        request.TravelGroup.Should().BeSameAs(group);
        request.GroupName.Should().Be(group.Name);
    }

    [Theory]
    [InlineData(0, 20, "Valid name")]
    [InlineData(10, 0, "Valid name")]
    [InlineData(10, 20, "")]
    public void Create_RejectsInvalidTravelGroupInvariant(
        long itineraryId,
        long hostUserId,
        string name)
    {
        var act = () => TravelGroup.Create(
            itineraryId,
            hostUserId,
            name,
            DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateHost_RejectsNonHostMember()
    {
        var group = TravelGroup.Create(10, 20, "Valid name", DateTimeOffset.UtcNow);

        var act = () => GroupMember.CreateHost(group, 21, DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddMember_RejectsDuplicateUser()
    {
        var group = TravelGroup.Create(10, 20, "Valid name", DateTimeOffset.UtcNow);
        group.AddMember(GroupMember.CreateHost(group, 20, DateTimeOffset.UtcNow));

        var act = () => group.AddMember(GroupMember.CreateHost(group, 20, DateTimeOffset.UtcNow));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CreationRequest_RejectsEmptyIdempotencyKey()
    {
        var group = TravelGroup.Create(10, 20, "Valid name", DateTimeOffset.UtcNow);

        var act = () => TravelGroupCreationRequest.Create(
            20,
            Guid.Empty,
            10,
            group.Name,
            group,
            DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ItineraryCreate_RejectsInvalidOwner()
    {
        var act = () => Itinerary.Create(
            0,
            "Trip",
            "Active",
            DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}