using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.Swagger;

using TripMate.Api.Controllers.V1.Requests;
using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Api.Realtime;
using TripMate.Application.Features.TravelGroups.Chat;
using TripMate.Application.Features.TravelGroups.Chat.MarkGroupChatRead;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class GroupChatHardeningEndpointTests
{
    [Fact]
    public async Task Notifications_RecheckActiveRecipients_AndCommittedSendsSurviveHubFailure()
    {
        var hub = new RecordingHub();
        await using var factory = new TripMateApiFactory(configureTestServices: services =>
            services.AddSingleton<IHubContext<GroupChatHub>>(hub));
        var (host, member, outsider, group, _) = await SeedTwoGroupsAsync(factory);
        using var client = factory.CreateAuthenticatedClient(host, UserRole.Traveler);
        var route = $"/api/v1/travel-groups/{group}/messages";
        (await client.PostAsJsonAsync(route, new SendGroupMessageRequest("broadcast", "One"))).StatusCode.Should().Be(HttpStatusCode.Created);
        hub.Users.Should().BeEquivalentTo([host.ToString(), member.ToString()]);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.GroupMembers.SingleAsync(m => m.GroupId == group && m.UserId == member)).Leave(DateTimeOffset.UtcNow);
            return await db.SaveChangesAsync();
        });
        (await client.PostAsJsonAsync(route, new SendGroupMessageRequest("text", "Two"))).StatusCode.Should().Be(HttpStatusCode.Created);
        hub.Users.Should().Equal(host.ToString());
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == outsider)).Status = AccountStatus.Locked;
            var currentGroup = await db.TravelGroups.SingleAsync(g => g.Id == group);
            db.GroupMembers.Add(GroupMember.CreateMember(currentGroup, outsider, DateTimeOffset.UtcNow));
            return await db.SaveChangesAsync();
        });
        // Recipient filtering is independently enforced even if authentication remains cached.
        hub.Fail = true;
        var saved = await client.PostAsJsonAsync(route, new SendGroupMessageRequest("text", "Three"));
        saved.StatusCode.Should().Be(HttpStatusCode.Created);
        hub.Users.Should().Equal(host.ToString());
        var history = await client.GetFromJsonAsync<List<GroupMessageDto>>(route);
        history!.Select(m => m.Content).Should().Contain("Three");
    }

    [Fact]
    public async Task Send_ExposesRetryKey_AndConflictReturns409()
    {
        await using var factory = new TripMateApiFactory();
        var (host, _, _, group, _) = await SeedTwoGroupsAsync(factory);
        using var client = factory.CreateAuthenticatedClient(host, UserRole.Traveler);
        var route = $"/api/v1/travel-groups/{group}/messages";
        var request = new SendGroupMessageRequest("text", "Hello", ClientMessageId: Guid.NewGuid());
        var first = await (await client.PostAsJsonAsync(route, request)).Content.ReadFromJsonAsync<GroupMessageDto>();
        var second = await (await client.PostAsJsonAsync(route, request)).Content.ReadFromJsonAsync<GroupMessageDto>();
        second!.MessageId.Should().Be(first!.MessageId);
        second.ClientMessageId.Should().Be(request.ClientMessageId);
        (await client.PostAsJsonAsync(route, request with { Content = "Changed" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ReadEndpoint_AdvancesCursorWithoutAcknowledgingBroadcast_AndRejectsCrossGroupMessage()
    {
        await using var factory = new TripMateApiFactory();
        var (host, member, outsider, group, otherGroup) = await SeedTwoGroupsAsync(factory);
        using var hostClient = factory.CreateAuthenticatedClient(host, UserRole.Traveler);
        using var memberClient = factory.CreateAuthenticatedClient(member, UserRole.Traveler);
        using var outsiderClient = factory.CreateAuthenticatedClient(outsider, UserRole.Traveler);
        var route = $"/api/v1/travel-groups/{group}/messages";
        var first = await (await hostClient.PostAsJsonAsync(route, new SendGroupMessageRequest("broadcast", "Notice"))).Content.ReadFromJsonAsync<GroupMessageDto>();
        var newer = await (await hostClient.PostAsJsonAsync(route, new SendGroupMessageRequest("text", "Latest"))).Content.ReadFromJsonAsync<GroupMessageDto>();
        var marked = await memberClient.PostAsJsonAsync(route + "/read", new MarkGroupChatReadRequest(newer!.MessageId));
        marked.StatusCode.Should().Be(HttpStatusCode.OK);
        (await marked.Content.ReadFromJsonAsync<GroupChatReadCursorDto>())!.LastReadChatMessageId.Should().Be(newer.MessageId);
        var older = await memberClient.PostAsJsonAsync(route + "/read", new MarkGroupChatReadRequest(first!.MessageId));
        (await older.Content.ReadFromJsonAsync<GroupChatReadCursorDto>())!.LastReadChatMessageId.Should().Be(newer.MessageId);
        (await outsiderClient.PostAsJsonAsync(route + "/read", new MarkGroupChatReadRequest(first.MessageId))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await memberClient.PostAsJsonAsync($"/api/v1/travel-groups/{otherGroup}/messages/read", new MarkGroupChatReadRequest(first.MessageId))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var history = await memberClient.GetFromJsonAsync<List<GroupMessageDto>>(route);
        history![0].ReadByCount.Should().Be(0);
    }

    private sealed class RecordingHub : IHubContext<GroupChatHub>
    {
        public List<string> Users { get; } = [];
        public bool Fail { get; set; }
        public IHubClients Clients => new RecordingClients(this);
        public IGroupManager Groups => throw new NotSupportedException();
        private sealed class RecordingClients(RecordingHub owner) : IHubClients, IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
                owner.Fail ? Task.FromException(new InvalidOperationException("Simulated unavailable hub")) : Task.CompletedTask;
            public IClientProxy Users(IReadOnlyList<string> ids) { owner.Users.Clear(); owner.Users.AddRange(ids); return this; }
            public IClientProxy All => throw new InvalidOperationException("Must target current users");
            public IClientProxy AllExcept(IReadOnlyList<string> ids) => throw new NotSupportedException();
            public IClientProxy Client(string id) => throw new NotSupportedException();
            public IClientProxy Clients(IReadOnlyList<string> ids) => throw new NotSupportedException();
            public IClientProxy Group(string id) => throw new InvalidOperationException("Room membership can be stale");
            public IClientProxy GroupExcept(string id, IReadOnlyList<string> ids) => throw new NotSupportedException();
            public IClientProxy Groups(IReadOnlyList<string> ids) => throw new NotSupportedException();
            public IClientProxy User(string id) => throw new NotSupportedException();
        }
    }
    private static async Task<(long HostUserId, long MemberUserId, long OutsiderUserId, long GroupAId, long GroupBId)> SeedTwoGroupsAsync(
        TripMateApiFactory factory)
    {
        return await factory.WithDbContextAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var host = new User
            {
                Email = $"chat-host-{Guid.NewGuid():N}@example.com",
                FullName = "Chat Host",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            var member = new User
            {
                Email = $"chat-member-{Guid.NewGuid():N}@example.com",
                FullName = "Chat Member",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            var outsider = new User
            {
                Email = $"chat-outsider-{Guid.NewGuid():N}@example.com",
                FullName = "Chat Outsider",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            db.Users.AddRange(host, member, outsider);
            await db.SaveChangesAsync();

            var groupA = TravelGroup.Create(1, host.Id, "Group A", now);
            var groupAM1 = GroupMember.CreateHost(groupA, host.Id, now);
            var groupAM2 = GroupMember.CreateMember(groupA, member.Id, now);
            groupA.AddMember(groupAM1);
            groupA.AddMember(groupAM2);

            var groupB = TravelGroup.Create(2, host.Id, "Group B", now);
            var groupBM1 = GroupMember.CreateHost(groupB, host.Id, now);
            var groupBM2 = GroupMember.CreateMember(groupB, member.Id, now);
            groupB.AddMember(groupBM1);
            groupB.AddMember(groupBM2);

            db.TravelGroups.AddRange(groupA, groupB);
            db.GroupMembers.AddRange(groupAM1, groupAM2, groupBM1, groupBM2);
            await db.SaveChangesAsync();

            return (host.Id, member.Id, outsider.Id, groupA.Id, groupB.Id);
        });
    }

}