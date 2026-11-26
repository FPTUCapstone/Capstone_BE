using FluentAssertions;

using TripMate.Application.Features.TravelGroups.GetConversations;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TravelGroups.GetConversations;

public sealed class GetTravelGroupConversationsQueryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_OnlyActiveMemberships_ProjectsActiveAndClosedGroupsWithRealMessagePreview()
    {
        await using var db = TestDbContext.Create();
        var active = await AddGroupAsync(db, 10);
        var closed = await AddGroupAsync(db, 10);
        var closedMembership = db.GroupMembers.Single(m => m.GroupId == closed.Id);
        closedMembership.Leave(Now);
        closed.Close(Now);
        closedMembership.Reactivate(Now);
        await AddGroupAsync(db, 20); // Someone else's group.
        var left = await AddGroupAsync(db, 10);
        db.GroupMembers.Single(m => m.GroupId == left.Id).Leave(Now);
        var removed = await AddGroupAsync(db, 10);
        db.GroupMembers.Single(m => m.GroupId == removed.Id).Remove(Now);
        db.GroupMembers.Add(GroupMember.CreateMember(active, 30, Now));
        var inactive = GroupMember.CreateMember(active, 40, Now);
        inactive.Remove(Now);
        db.GroupMembers.Add(inactive);
        db.GroupMessages.Add(new GroupMessage(active.Id, 10, nameof(GroupMessageType.Text), "Earlier", null, Now.AddMinutes(-1)));
        db.GroupMessages.Add(new GroupMessage(active.Id, 10, nameof(GroupMessageType.Text), "Latest", null, Now));
        typeof(Itinerary).GetProperty(nameof(Itinerary.SourceType))!
            .SetValue(active.Itinerary, Itinerary.BookedTourSourceType);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await new GetTravelGroupConversationsQueryHandler(db).Handle(
            new GetTravelGroupConversationsQuery(10), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Select(i => i.GroupId).Should().Equal(closed.Id, active.Id);
        result.Value.NextBeforeGroupId.Should().BeNull();
        var item = result.Value.Items[1];
        item.GroupName.Should().Be("Real group");
        item.ItineraryId.Should().Be(active.ItineraryId);
        item.ItineraryTitle.Should().Be("Real itinerary");
        item.ItinerarySourceType.Should().Be("BookedTour");
        item.HostUserId.Should().Be(10);
        item.MemberCount.Should().Be(2);
        item.Status.Should().Be("Active");
        item.LastMessage.Should().Be("Latest");
        item.LastMessageAtUtc.Should().Be(Now);
        result.Value.Items[0].Status.Should().Be("Closed");
        result.Value.Items[0].ItinerarySourceType.Should().Be("Manual");
        result.Value.Items[0].LastMessage.Should().BeNull();
        result.Value.Items[0].LastMessageAtUtc.Should().BeNull();
        db.ChangeTracker.Entries().Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_EmptyGroup_UnreadCountIsZero()
    {
        await using var db = TestDbContext.Create();
        await AddGroupAsync(db, 10);

        var result = await new GetTravelGroupConversationsQueryHandler(db).Handle(
            new GetTravelGroupConversationsQuery(10), CancellationToken.None);

        result.Value.Items.Should().ContainSingle().Which.UnreadCount.Should().Be(0);
    }

    [Theory]
    [InlineData(0, 2)] // Null cursor: all incoming messages.
    [InlineData(1, 1)] // Exact boundary is already read.
    [InlineData(2, 0)] // All incoming messages read, later own message excluded.
    [InlineData(3, 0)] // Cursor beyond the last message.
    public async Task Handle_UnreadCount_UsesOnlyCurrentMembershipCursorAndIncomingMessages(int cursor, int expected)
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db, 20);
        var unrelated = await AddGroupAsync(db, 20);
        db.GroupMembers.Single(m => m.GroupId == group.Id && m.UserId == 20)
            .AdvanceChatReadCursor(long.MaxValue);
        db.GroupMembers.Add(GroupMember.CreateMember(group, 10, Now));
        var first = new GroupMessage(group.Id, 20, nameof(GroupMessageType.Text), "First incoming", null, Now);
        db.GroupMessages.Add(first);
        await db.SaveChangesAsync();
        var second = new GroupMessage(group.Id, 20, nameof(GroupMessageType.Text), "Second incoming", null, Now);
        db.GroupMessages.Add(second);
        await db.SaveChangesAsync();
        db.GroupMessages.Add(new GroupMessage(group.Id, 10, nameof(GroupMessageType.Text), "Own later message", null, Now));
        db.GroupMessages.Add(new GroupMessage(unrelated.Id, 20, nameof(GroupMessageType.Text), "Unrelated incoming", null, Now));
        if (cursor != 0)
        {
            db.GroupMembers.Single(m => m.GroupId == group.Id && m.UserId == 10)
                .AdvanceChatReadCursor(cursor == 1 ? first.Id : cursor == 2 ? second.Id : long.MaxValue);
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await new GetTravelGroupConversationsQueryHandler(db).Handle(
            new GetTravelGroupConversationsQuery(10, 1), CancellationToken.None);

        result.Value.Items.Should().ContainSingle().Which.UnreadCount.Should().Be(expected);
        result.Value.Items[0].GroupId.Should().Be(group.Id);
        result.Value.NextBeforeGroupId.Should().BeNull();
        db.ChangeTracker.Entries().Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ActiveNonHostMember_SeesSharedGroupAndOutsiderSeesEmptyPage()
    {
        await using var db = TestDbContext.Create();
        var group = await AddGroupAsync(db, 20);
        db.GroupMembers.Add(GroupMember.CreateMember(group, 10, Now));
        await db.SaveChangesAsync();
        var handler = new GetTravelGroupConversationsQueryHandler(db);

        var member = await handler.Handle(new GetTravelGroupConversationsQuery(10), CancellationToken.None);
        member.Value.Items.Should().ContainSingle(item => item.GroupId == group.Id && item.HostUserId == 20);
        member.Value.Items[0].MemberCount.Should().Be(2);
        var outsider = await handler.Handle(new GetTravelGroupConversationsQuery(30), CancellationToken.None);
        outsider.Value.Items.Should().BeEmpty();
        outsider.Value.NextBeforeGroupId.Should().BeNull();
    }

    [Fact]
    public async Task Handle_CursorPages_UsesExclusiveDescendingGroupIdsAndSurvivesNewGroups()
    {
        await using var db = TestDbContext.Create();
        var first = await AddGroupAsync(db, 10);
        var second = await AddGroupAsync(db, 10);
        var third = await AddGroupAsync(db, 10);
        var handler = new GetTravelGroupConversationsQueryHandler(db);

        var page = await handler.Handle(new GetTravelGroupConversationsQuery(10, 2), CancellationToken.None);
        page.Value.Items.Select(i => i.GroupId).Should().Equal(third.Id, second.Id);
        page.Value.NextBeforeGroupId.Should().Be(second.Id);
        await AddGroupAsync(db, 10);
        var next = await handler.Handle(new GetTravelGroupConversationsQuery(10, 2, page.Value.NextBeforeGroupId), CancellationToken.None);
        next.Value.Items.Select(i => i.GroupId).Should().Equal(first.Id);
        next.Value.NextBeforeGroupId.Should().BeNull();
        var empty = await handler.Handle(new GetTravelGroupConversationsQuery(10, 2, first.Id), CancellationToken.None);
        empty.Value.Items.Should().BeEmpty();
        empty.Value.NextBeforeGroupId.Should().BeNull();
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(101, null)]
    [InlineData(int.MaxValue, null)]
    [InlineData(50, 0L)]
    [InlineData(50, -1L)]
    public async Task Handle_InvalidPaging_ReturnsFailure(int limit, long? beforeGroupId)
    {
        await using var db = TestDbContext.Create();
        var result = await new GetTravelGroupConversationsQueryHandler(db).Handle(
            new GetTravelGroupConversationsQuery(10, limit, beforeGroupId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    public async Task Handle_DefaultAndMaximumLimits_ReturnBoundedPages(int limit)
    {
        await using var db = TestDbContext.Create();
        for (var index = 0; index < 101; index++)
        {
            await AddGroupAsync(db, 10);
        }

        var query = limit == 50 ? new GetTravelGroupConversationsQuery(10) : new GetTravelGroupConversationsQuery(10, limit);
        var page = await new GetTravelGroupConversationsQueryHandler(db).Handle(query, CancellationToken.None);

        page.Value.Items.Should().HaveCount(limit);
        page.Value.NextBeforeGroupId.Should().Be(page.Value.Items[^1].GroupId);
    }

    private static async Task<TravelGroup> AddGroupAsync(TestDbContext db, long userId)
    {
        var itinerary = Itinerary.CreateManual(userId, "Real itinerary", Itinerary.ActiveStatus, Now);
        db.Itineraries.Add(itinerary);
        await db.SaveChangesAsync();
        var group = TravelGroup.Create(itinerary.Id, userId, "Real group", Now);
        db.TravelGroups.Add(group);
        db.GroupMembers.Add(GroupMember.CreateHost(group, userId, Now));
        await db.SaveChangesAsync();
        return group;
    }
}