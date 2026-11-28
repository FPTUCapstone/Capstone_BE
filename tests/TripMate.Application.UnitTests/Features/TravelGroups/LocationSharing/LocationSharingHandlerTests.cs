using FluentAssertions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Application.Features.TravelGroups.LocationSharing;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.TravelGroups.LocationSharing;

public sealed class LocationSharingHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Update_ActiveMember_PersistsTheirOwnChoice()
    {
        await using var db = TestDbContext.Create();
        var group = await SeedGroupAsync(db);
        var handler = new UpdateLocationSharingCommandHandler(db, new FixedClock());

        var enabled = await handler.Handle(
            new UpdateLocationSharingCommand(group.Id, 2, true), CancellationToken.None);

        enabled.IsSuccess.Should().BeTrue();
        enabled.Value.Enabled.Should().BeTrue();
        enabled.Value.UpdatedAtUtc.Should().Be(Now);
        (await db.GroupMembers.FindAsync(group.Id, 2L))!.LocationSharingEnabled.Should().BeTrue();

        var disabled = await handler.Handle(
            new UpdateLocationSharingCommand(group.Id, 2, false), CancellationToken.None);
        disabled.IsSuccess.Should().BeTrue();
        disabled.Value.Enabled.Should().BeFalse();
        (await db.GroupMembers.FindAsync(group.Id, 2L))!.LocationSharingEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Update_OutsideMember_CannotChangeAnotherMembersChoice()
    {
        await using var db = TestDbContext.Create();
        var group = await SeedGroupAsync(db);
        var handler = new UpdateLocationSharingCommandHandler(db, new FixedClock());

        var result = await handler.Handle(
            new UpdateLocationSharingCommand(group.Id, 3, true), CancellationToken.None);

        result.ErrorCode.Should().Be(TravelGroupErrorCodes.ActiveMembershipRequired);
        (await db.GroupMembers.FindAsync(group.Id, 2L))!.LocationSharingEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Update_Disable_DeletesPreviouslySharedCoordinates()
    {
        await using var db = TestDbContext.Create();
        var group = await SeedGroupAsync(db);
        var member = (await db.GroupMembers.FindAsync(group.Id, 2L))!;
        member.SetLocationSharing(true, Now.AddMinutes(-1));
        db.GroupLocations.Add(GroupLocation.Create(group.Id, 2, 16m, 108m, Now));
        await db.SaveChangesAsync();

        var result = await new UpdateLocationSharingCommandHandler(db, new FixedClock()).Handle(
            new UpdateLocationSharingCommand(group.Id, 2, false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.GroupLocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_ActiveMember_ReturnsPersistedState()
    {
        await using var db = TestDbContext.Create();
        var group = await SeedGroupAsync(db);
        var update = new UpdateLocationSharingCommandHandler(db, new FixedClock());
        await update.Handle(new UpdateLocationSharingCommand(group.Id, 2, true), CancellationToken.None);

        var result = await new GetLocationSharingQueryHandler(db).Handle(
            new GetLocationSharingQuery(group.Id, 2), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Enabled.Should().BeTrue();
        result.Value.UpdatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public async Task GetEnabledGroups_ReturnsOnlyTheTravelersOwnActiveOptIns()
    {
        await using var db = TestDbContext.Create();
        var group = await SeedGroupAsync(db);
        var member = (await db.GroupMembers.FindAsync(group.Id, 2L))!;
        member.SetLocationSharing(true, Now);
        await db.SaveChangesAsync();

        var mine = await new GetEnabledLocationSharingGroupsQueryHandler(db).Handle(
            new GetEnabledLocationSharingGroupsQuery(2), CancellationToken.None);
        var outsider = await new GetEnabledLocationSharingGroupsQueryHandler(db).Handle(
            new GetEnabledLocationSharingGroupsQuery(99), CancellationToken.None);

        mine.Value.GroupIds.Should().Equal(group.Id);
        outsider.Value.GroupIds.Should().BeEmpty();
    }

    [Fact]
    public async Task LockedAccountsAndClosedGroupsCannotEnableLocationSharing()
    {
        await using var db = TestDbContext.Create();
        var group = await SeedGroupAsync(db);
        db.Users.Local.Single().Status = TripMate.Domain.Enums.AccountStatus.Locked;
        await db.SaveChangesAsync();
        var handler = new UpdateLocationSharingCommandHandler(db, new FixedClock());
        (await handler.Handle(new(group.Id, 2, true), default)).IsFailure.Should().BeTrue();
        db.Users.Local.Single().Status = TripMate.Domain.Enums.AccountStatus.Active;
        db.Entry(group).Property(g => g.Status).CurrentValue = TripMate.Domain.Enums.TravelGroupStatus.Closed;
        await db.SaveChangesAsync();
        (await handler.Handle(new(group.Id, 2, true), default)).IsFailure.Should().BeTrue();
        (await db.GroupMembers.FindAsync(group.Id, 2L))!.LocationSharingEnabled.Should().BeFalse();
    }

    private static async Task<TravelGroup> SeedGroupAsync(TestDbContext db)
    {
        db.Users.Add(new User { Id = 2, FullName = "Location member", Email = "location@example.test", Status = TripMate.Domain.Enums.AccountStatus.Active, Role = TripMate.Domain.Enums.UserRole.Traveler });
        var group = TravelGroup.Create(1, 2, "Group", Now);
        db.TravelGroups.Add(group);
        db.GroupMembers.Add(GroupMember.CreateHost(group, 2, Now));
        await db.SaveChangesAsync();
        return group;
    }

    private sealed class FixedClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => Now;
    }
}