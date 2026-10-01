using FluentAssertions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Application.Features.TravelGroups.LocationSharing;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.TravelGroups.LocationSharing;

public sealed class GroupLocationHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DelayedUploadFromClearedSession_IsRejectedWhileOptInRemains()
    {
        await using var db = TestDbContext.Create();
        var group = await SeedGroupAsync(db);
        var member = (await db.GroupMembers.FindAsync(group.Id, 2L))!;
        member.SetLocationSharing(true, Now.AddMinutes(-1));
        await db.SaveChangesAsync();
        var oldVersion = member.LocationSharingUpdatedAtUtc!.Value.UtcTicks.ToString();

        var cleared = await new ClearGroupLocationCommandHandler(db, new FixedClock()).Handle(
            new ClearGroupLocationCommand(group.Id, 2), CancellationToken.None);
        var delayed = await new PublishGroupLocationCommandHandler(db, new FixedClock()).Handle(
            new PublishGroupLocationCommand(group.Id, 2, 16m, 108m, oldVersion), CancellationToken.None);

        cleared.IsSuccess.Should().BeTrue();
        member.LocationSharingEnabled.Should().BeTrue();
        delayed.ErrorCode.Should().Be(TravelGroupErrorCodes.LocationSharingSessionExpired);
        db.GroupLocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Publish_RequiresExplicitOptIn()
    {
        await using var db = TestDbContext.Create();
        var group = await SeedGroupAsync(db);

        var result = await new PublishGroupLocationCommandHandler(db, new FixedClock()).Handle(
            new PublishGroupLocationCommand(group.Id, 2, 16.047079m, 108.206230m),
            CancellationToken.None);

        result.ErrorCode.Should().Be(TravelGroupErrorCodes.LocationSharingNotEnabled);
        db.GroupLocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Publish_OptedInMember_IsVisibleOnlyToActiveMember()
    {
        await using var db = TestDbContext.Create();
        var group = await SeedGroupAsync(db);
        var member = (await db.GroupMembers.FindAsync(group.Id, 2L))!;
        member.SetLocationSharing(true, Now.AddMinutes(-1));
        await db.SaveChangesAsync();

        var published = await new PublishGroupLocationCommandHandler(db, new FixedClock()).Handle(
            new PublishGroupLocationCommand(group.Id, 2, 16.047079m, 108.206230m,
                member.LocationSharingUpdatedAtUtc!.Value.UtcTicks.ToString()),
            CancellationToken.None);
        var visible = await new GetGroupLocationsQueryHandler(db, new FixedClock()).Handle(
            new GetGroupLocationsQuery(group.Id, 2), CancellationToken.None);
        var outsider = await new GetGroupLocationsQueryHandler(db, new FixedClock()).Handle(
            new GetGroupLocationsQuery(group.Id, 99), CancellationToken.None);

        published.IsSuccess.Should().BeTrue();
        visible.Value.Locations.Should().ContainSingle();
        visible.Value.Locations[0].Latitude.Should().Be(16.047079m);
        visible.Value.Locations[0].Longitude.Should().Be(108.206230m);
        outsider.ErrorCode.Should().Be(TravelGroupErrorCodes.ActiveMembershipRequired);
    }

    [Fact]
    public async Task Get_AfterOptOut_DoesNotExposeOldPosition()
    {
        await using var db = TestDbContext.Create();
        var group = await SeedGroupAsync(db);
        var member = (await db.GroupMembers.FindAsync(group.Id, 2L))!;
        member.SetLocationSharing(true, Now.AddMinutes(-1));
        db.GroupLocations.Add(GroupLocation.Create(group.Id, 2, 16.047079m, 108.206230m, Now));
        await db.SaveChangesAsync();
        member.SetLocationSharing(false, Now.AddSeconds(1));
        await db.SaveChangesAsync();

        var result = await new GetGroupLocationsQueryHandler(db, new FixedClock()).Handle(
            new GetGroupLocationsQuery(group.Id, 2), CancellationToken.None);

        result.Value.Locations.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_ExcludesStaleAndPreOptInPositions()
    {
        await using var db = TestDbContext.Create();
        var group = await SeedGroupAsync(db);
        var member = (await db.GroupMembers.FindAsync(group.Id, 2L))!;
        member.SetLocationSharing(true, Now.AddMinutes(-1));
        db.GroupLocations.Add(GroupLocation.Create(group.Id, 2, 16m, 108m, Now.AddMinutes(-3)));
        await db.SaveChangesAsync();

        var result = await new GetGroupLocationsQueryHandler(db, new FixedClock()).Handle(
            new GetGroupLocationsQuery(group.Id, 2), CancellationToken.None);

        result.Value.Locations.Should().BeEmpty();
    }

    private static async Task<TravelGroup> SeedGroupAsync(TestDbContext db)
    {
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