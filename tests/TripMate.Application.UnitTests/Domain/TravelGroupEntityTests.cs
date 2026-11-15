using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Domain;

public sealed class TravelGroupEntityTests
{
    [Fact]
    public void NewGroup_IsActiveUntilLastHostClosesIt()
    {
        var createdAt = new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);
        var closedAt = createdAt.AddHours(1);
        var group = TravelGroup.Create(10, 20, "Trip", createdAt);

        group.Status.Should().Be(TravelGroupStatus.Active);
        group.ClosedAtUtc.Should().BeNull();

        group.Close(closedAt);

        group.Status.Should().Be(TravelGroupStatus.Closed);
        group.ClosedAtUtc.Should().Be(closedAt);
        group.HostUserId.Should().Be(20);
        var closeAgain = () => group.Close(closedAt.AddMinutes(1));
        closeAgain.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Close_RequiresEveryMemberToHaveLeft()
    {
        var now = DateTimeOffset.UtcNow;
        var group = TravelGroup.Create(10, 20, "Trip", now);
        var host = GroupMember.CreateHost(group, 20, now);
        group.AddMember(host);

        var closeWithActiveHost = () => group.Close(now.AddMinutes(5));
        closeWithActiveHost.Should().Throw<InvalidOperationException>();
        group.Status.Should().Be(TravelGroupStatus.Active);

        host.Leave(now.AddMinutes(5));
        group.Close(now.AddMinutes(5));
        group.Status.Should().Be(TravelGroupStatus.Closed);
    }

    [Fact]
    public void ActiveHost_CanTransferHostToAnotherActiveMember()
    {
        var now = DateTimeOffset.UtcNow;
        var group = TravelGroup.Create(10, 20, "Trip", now);
        var successor = GroupMember.CreateMember(group, 21, now);
        group.AddMember(GroupMember.CreateHost(group, 20, now));
        group.AddMember(successor);

        group.TransferHostTo(successor);

        group.HostUserId.Should().Be(21);
        group.Status.Should().Be(TravelGroupStatus.Active);
    }

    [Fact]
    public void TransferHost_RejectsMemberNotActuallyAddedToTheGroup()
    {
        var now = DateTimeOffset.UtcNow;
        var group = TravelGroup.Create(10, 20, "Trip", now);
        group.AddMember(GroupMember.CreateHost(group, 20, now));
        var unjoinedTraveler = GroupMember.CreateMember(group, 21, now);

        var act = () => group.TransferHostTo(unjoinedTraveler);

        act.Should().Throw<InvalidOperationException>();
        group.HostUserId.Should().Be(20);
    }

    [Fact]
    public void LeavingMember_DisablesLocationSharingAndCannotLeaveTwice()
    {
        var now = DateTimeOffset.UtcNow;
        var group = TravelGroup.Create(10, 20, "Trip", now);
        var member = GroupMember.CreateMember(group, 21, now);
        var leftAt = now.AddMinutes(5);

        member.Leave(leftAt);

        member.Status.Should().Be(GroupMemberStatus.Left);
        member.LeftAtUtc.Should().Be(leftAt);
        member.LocationSharingEnabled.Should().BeFalse();
        var leaveAgain = () => member.Leave(leftAt.AddMinutes(1));
        leaveAgain.Should().Throw<InvalidOperationException>();
    }

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
    public void CreateManual_WithValidData_CreatesItinerary()
    {
        var now = DateTimeOffset.UtcNow;

        var itinerary = Itinerary.CreateManual(10, "Da Nang Trip", "Active", now);

        itinerary.TravelerUserId.Should().Be(10);
        itinerary.SourceType.Should().Be("Manual");
        itinerary.Title.Should().Be("Da Nang Trip");
        itinerary.Status.Should().Be("Active");
        itinerary.CreatedAtUtc.Should().Be(now);
        itinerary.UpdatedAtUtc.Should().Be(now);
    }

    [Fact]
    public void CreateManual_TrimsTitleAndStatus()
    {
        var itinerary = Itinerary.CreateManual(
            10,
            "  Da Nang Trip  ",
            " Active ",
            DateTimeOffset.UtcNow);

        itinerary.Title.Should().Be("Da Nang Trip");
        itinerary.Status.Should().Be("Active");
    }

    [Fact]
    public void CreateManual_WithTitleLongerThan200_Throws()
    {
        var act = () => Itinerary.CreateManual(
            10,
            new string('a', 201),
            "Active",
            DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("active")]
    [InlineData("Archived")]
    public void CreateManual_WithUnsupportedStatus_Throws(string status)
    {
        var act = () => Itinerary.CreateManual(
            10,
            "Da Nang Trip",
            status,
            DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateManual_WithInvalidTravelerId_Throws()
    {
        var act = () => Itinerary.CreateManual(
            0,
            "Trip",
            "Active",
            DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}