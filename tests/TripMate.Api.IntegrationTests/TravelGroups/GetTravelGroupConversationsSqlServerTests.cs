using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.TravelGroups.GetConversations;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class GetTravelGroupConversationsSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_RealSql_ProjectsMembershipPreviewAndCursorInOneUntrackedRead()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var now = new DateTimeOffset(2026, 11, 23, 17, 0, 0, TimeSpan.FromHours(7));
        long hostId;
        long olderId;
        long newerId;
        await using (var setup = database.CreateDbContext())
        {
            var host = new User
            {
                Email = $"{Guid.NewGuid():N}@example.com",
                FullName = "Conversation SQL host",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            setup.Users.Add(host);
            var sender = new User
            {
                Email = $"{Guid.NewGuid():N}@example.com",
                FullName = "Conversation SQL sender",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            setup.Users.Add(sender);
            await setup.SaveChangesAsync();
            hostId = host.Id;
            var itinerary = Itinerary.CreateManual(hostId, null, Itinerary.ActiveStatus, now);
            setup.Itineraries.Add(itinerary);
            await setup.SaveChangesAsync();
            var older = TravelGroup.Create(itinerary.Id, hostId, "Older SQL group", now);
            setup.TravelGroups.Add(older);
            setup.GroupMembers.Add(GroupMember.CreateHost(older, hostId, now));
            await setup.SaveChangesAsync();
            olderId = older.Id;
            var newer = TravelGroup.Create(itinerary.Id, hostId, "Newer SQL group", now);
            setup.TravelGroups.Add(newer);
            setup.GroupMembers.Add(GroupMember.CreateHost(newer, hostId, now));
            setup.GroupMembers.Add(GroupMember.CreateMember(newer, sender.Id, now));
            await setup.SaveChangesAsync();
            newerId = newer.Id;
            var firstMessage = new GroupMessage(newerId, sender.Id, nameof(GroupMessageType.Text), "First SQL message", null, now.AddMinutes(-1));
            setup.GroupMessages.Add(firstMessage);
            await setup.SaveChangesAsync();
            setup.GroupMembers.Single(m => m.GroupId == newerId && m.UserId == hostId)
                .AdvanceChatReadCursor(firstMessage.Id);
            setup.GroupMessages.Add(new GroupMessage(newerId, sender.Id, nameof(GroupMessageType.Text), "Latest SQL message", null, now));
            await setup.SaveChangesAsync();
        }

        var counter = new TestCommandCounterInterceptor();
        await using var db = database.CreateDbContext(counter);
        var handler = new GetTravelGroupConversationsQueryHandler(db);
        var first = await handler.Handle(new GetTravelGroupConversationsQuery(hostId, 1), CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        first.Value.Items.Should().ContainSingle();
        var item = first.Value.Items[0];
        item.GroupId.Should().Be(newerId);
        item.ItineraryTitle.Should().BeNull();
        item.ItinerarySourceType.Should().Be("Manual");
        item.MemberCount.Should().Be(2);
        item.UnreadCount.Should().Be(1);
        item.Status.Should().Be("Active");
        item.LastMessage.Should().Be("Latest SQL message");
        item.LastMessageAtUtc.Should().Be(now.ToUniversalTime());
        item.LastMessageAtUtc!.Value.Offset.Should().Be(TimeSpan.Zero);
        first.Value.NextBeforeGroupId.Should().Be(newerId);
        counter.CommandCount.Should().Be(1);
        db.ChangeTracker.Entries().Should().BeEmpty();

        counter.Reset();
        var second = await handler.Handle(new GetTravelGroupConversationsQuery(hostId, 1, newerId), CancellationToken.None);
        second.Value.Items.Should().ContainSingle(i => i.GroupId == olderId);
        second.Value.Items[0].LastMessage.Should().BeNull();
        second.Value.Items[0].LastMessageAtUtc.Should().BeNull();
        second.Value.Items[0].UnreadCount.Should().Be(0);
        second.Value.NextBeforeGroupId.Should().BeNull();
        counter.CommandCount.Should().Be(1);

        var outsider = await handler.Handle(new GetTravelGroupConversationsQuery(hostId + 100), CancellationToken.None);
        outsider.Value.Items.Should().BeEmpty();
        db.ChangeTracker.Entries().Should().BeEmpty();
    }
}