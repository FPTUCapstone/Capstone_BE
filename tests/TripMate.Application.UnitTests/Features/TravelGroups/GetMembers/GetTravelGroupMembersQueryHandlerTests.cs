using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Application.Features.TravelGroups.GetMembers;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TravelGroups.GetMembers;

public sealed class GetTravelGroupMembersQueryHandlerTests
{
    [Fact]
    public async Task Handle_MissingGroup_ReturnsGroupNotFound()
    {
        await using var db = TestDbContext.Create();
        var handler = CreateHandler(db);

        var result = await handler.Handle(
            new GetTravelGroupMembersQuery(404, 101),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelGroupErrorCodes.GroupNotFound);
    }

    [Fact]
    public async Task Handle_NonMember_ReturnsActiveMembershipRequired()
    {
        await using var db = TestDbContext.Create();
        var host = await AddTravelerAsync(db, "Host Traveler");
        var outsider = await AddTravelerAsync(db, "Outside Traveler");
        var group = TravelGroup.Create(10, host.Id, "Da Nang Weekend", DateTimeOffset.UtcNow);
        db.TravelGroups.Add(group);
        db.GroupMembers.Add(GroupMember.CreateHost(group, host.Id, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var handler = CreateHandler(db);

        var result = await handler.Handle(
            new GetTravelGroupMembersQuery(group.Id, outsider.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelGroupErrorCodes.ActiveMembershipRequired);
    }

    [Fact]
    public async Task Handle_RemovedMember_ReturnsActiveMembershipRequired()
    {
        await using var db = TestDbContext.Create();
        var host = await AddTravelerAsync(db, "Host Traveler");
        var removedTraveler = await AddTravelerAsync(db, "Removed Traveler");
        var group = TravelGroup.Create(10, host.Id, "Da Nang Weekend", DateTimeOffset.UtcNow);
        db.TravelGroups.Add(group);
        await db.SaveChangesAsync();

        db.GroupMembers.Add(GroupMember.CreateHost(group, host.Id, DateTimeOffset.UtcNow));
        var removedMembership = GroupMember.CreateMember(
            group,
            removedTraveler.Id,
            DateTimeOffset.UtcNow);
        typeof(GroupMember).GetProperty(nameof(GroupMember.Status))!
            .SetValue(removedMembership, GroupMemberStatus.Removed);
        db.GroupMembers.Add(removedMembership);
        await db.SaveChangesAsync();

        var handler = CreateHandler(db);

        var result = await handler.Handle(
            new GetTravelGroupMembersQuery(group.Id, removedTraveler.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelGroupErrorCodes.ActiveMembershipRequired);
    }

    [Fact]
    public async Task Handle_ActiveMembershipsWithoutHost_ReturnsMemberListInconsistent()
    {
        await using var db = TestDbContext.Create();
        var host = await AddTravelerAsync(db, "Host Traveler");
        var member = await AddTravelerAsync(db, "Active Traveler");
        var group = TravelGroup.Create(10, host.Id, "Da Nang Weekend", DateTimeOffset.UtcNow);
        db.TravelGroups.Add(group);
        await db.SaveChangesAsync();
        db.GroupMembers.Add(GroupMember.CreateMember(group, member.Id, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var handler = CreateHandler(db);

        var result = await handler.Handle(
            new GetTravelGroupMembersQuery(group.Id, member.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelGroupErrorCodes.MemberListInconsistent);
        result.ErrorMessage.Should().Be("Unable to load group members.");
    }

    [Fact]
    public async Task Handle_ActiveMember_ReturnsOnlyActiveMembersWithExactlyOneHost()
    {
        await using var db = TestDbContext.Create();
        var host = await AddTravelerAsync(db, "Host Traveler");
        var activeMember = await AddTravelerAsync(db, "Active Traveler");
        var removedMember = await AddTravelerAsync(db, "Removed Traveler");
        var group = TravelGroup.Create(10, host.Id, "Da Nang Weekend", DateTimeOffset.UtcNow);
        db.TravelGroups.Add(group);
        await db.SaveChangesAsync();

        db.GroupMembers.Add(GroupMember.CreateHost(group, host.Id, DateTimeOffset.UtcNow.AddHours(-2)));
        db.GroupMembers.Add(GroupMember.CreateMember(group, activeMember.Id, DateTimeOffset.UtcNow.AddHours(-1)));
        var removedMembership = GroupMember.CreateMember(group, removedMember.Id, DateTimeOffset.UtcNow);
        typeof(GroupMember).GetProperty(nameof(GroupMember.Status))!
            .SetValue(removedMembership, GroupMemberStatus.Removed);
        db.GroupMembers.Add(removedMembership);
        await db.SaveChangesAsync();

        var handler = CreateHandler(db);

        var result = await handler.Handle(
            new GetTravelGroupMembersQuery(group.Id, activeMember.Id),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.GroupId.Should().Be(group.Id);
        result.Value.MemberCount.Should().Be(2);
        result.Value.Members.Should().ContainSingle(member => member.IsHost);
        result.Value.Members.Should().HaveCount(2);
        result.Value.Members.Select(member => member.DisplayName)
            .Should().BeEquivalentTo([host.FullName, activeMember.FullName]);
    }

    private static async Task<User> AddTravelerAsync(TestDbContext db, string fullName)
    {
        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@example.com",
            FullName = fullName,
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static GetTravelGroupMembersQueryHandler CreateHandler(TestDbContext db) =>
        new(db, NullLogger<GetTravelGroupMembersQueryHandler>.Instance);
}