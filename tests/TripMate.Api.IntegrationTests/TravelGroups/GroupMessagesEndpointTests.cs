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
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class GroupMessagesEndpointTests
{
    [Fact]
    public async Task MessagesEndpoints_RequireTravelerOrOperatorAuth_AndDenyOutsiders()
    {
        await using var factory = new TripMateApiFactory();
        var (hostUserId, memberUserId, outsiderUserId, groupId, _) = await SeedTwoGroupsAsync(factory);

        using var anonymous = factory.CreateClient();
        (await anonymous.GetAsync($"/api/v1/travel-groups/{groupId}/messages"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync(
                $"/api/v1/travel-groups/{groupId}/messages",
                new SendGroupMessageRequest("text", "Hello")))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var adminClient = factory.CreateAuthenticatedClient(hostUserId, UserRole.Administrator);
        (await adminClient.GetAsync($"/api/v1/travel-groups/{groupId}/messages"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var outsiderClient = factory.CreateAuthenticatedClient(outsiderUserId, UserRole.Traveler);
        (await outsiderClient.GetAsync($"/api/v1/travel-groups/{groupId}/messages"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await outsiderClient.PostAsJsonAsync(
                $"/api/v1/travel-groups/{groupId}/messages",
                new SendGroupMessageRequest("text", "Sneak in")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var memberClient = factory.CreateAuthenticatedClient(memberUserId, UserRole.Traveler);
        (await memberClient.GetAsync($"/api/v1/travel-groups/{groupId}/messages"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MessagesEndpoints_RejectInvalidInputs_AndEnforceCrossGroupIdor_AndClosedGroup()
    {
        await using var factory = new TripMateApiFactory();
        var (hostUserId, memberUserId, _, groupA, groupB) = await SeedTwoGroupsAsync(factory);
        using var hostClient = factory.CreateAuthenticatedClient(hostUserId, UserRole.Traveler);
        using var memberClient = factory.CreateAuthenticatedClient(memberUserId, UserRole.Traveler);

        (await hostClient.GetAsync("/api/v1/travel-groups/0/messages"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await hostClient.PostAsJsonAsync(
                "/api/v1/travel-groups/0/messages",
                new SendGroupMessageRequest("text", "Hi")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await hostClient.PostAsJsonAsync(
                $"/api/v1/travel-groups/{groupA}/messages",
                new SendGroupMessageRequest("text", "   ")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await hostClient.PostAsJsonAsync(
                $"/api/v1/travel-groups/{groupA}/messages",
                new SendGroupMessageRequest("unknown_type", "Hello")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var postInGroupB = await hostClient.PostAsJsonAsync(
            $"/api/v1/travel-groups/{groupB}/messages",
            new SendGroupMessageRequest("broadcast", "Group B Alert"));
        postInGroupB.StatusCode.Should().Be(HttpStatusCode.Created);
        var groupBMsg = await postInGroupB.Content.ReadFromJsonAsync<GroupMessageDto>();

        // Cross-group IDOR: member of BOTH groups tries to acknowledge Group B's message under Group A's URL
        var idorResponse = await memberClient.PostAsync(
            $"/api/v1/travel-groups/{groupA}/messages/{groupBMsg!.MessageId}/acknowledge",
            null);
        idorResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Wrong message type interaction: POST /checkin on a broadcast message
        var wrongTypeResponse = await memberClient.PostAsync(
            $"/api/v1/travel-groups/{groupB}/messages/{groupBMsg.MessageId}/checkin",
            null);
        wrongTypeResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Poll boundary option index (-1 and 6)
        var pollCreate = await hostClient.PostAsJsonAsync(
            $"/api/v1/travel-groups/{groupA}/messages",
            new SendGroupMessageRequest("poll", "Lunch?"));
        var pollMsg = await pollCreate.Content.ReadFromJsonAsync<GroupMessageDto>();

        (await memberClient.PostAsJsonAsync(
                $"/api/v1/travel-groups/{groupA}/messages/{pollMsg!.MessageId}/vote",
                new VotePollRequest(-1)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await memberClient.PostAsJsonAsync(
                $"/api/v1/travel-groups/{groupA}/messages/{pollMsg.MessageId}/vote",
                new VotePollRequest(6)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Closed group returns 409 Conflict
        await factory.WithDbContextAsync(async db =>
        {
            var group = await db.TravelGroups
                .Include(g => g.GroupMembers)
                .FirstAsync(g => g.Id == groupA);
            var now = DateTimeOffset.UtcNow;
            foreach (var m in group.GroupMembers.Where(m => m.Status == GroupMemberStatus.Active))
            {
                m.Leave(now);
            }
            group.Close(now);
            await db.SaveChangesAsync();
            return true;
        });

        (await hostClient.PostAsJsonAsync(
                $"/api/v1/travel-groups/{groupA}/messages",
                new SendGroupMessageRequest("text", "Closed group message")))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task EndToEndHttpWorkflow_PersistsAndAggregatesAllMessageTypes()
    {
        await using var factory = new TripMateApiFactory();
        var (hostUserId, memberUserId, _, groupId, _) = await SeedTwoGroupsAsync(factory);
        using var hostClient = factory.CreateAuthenticatedClient(hostUserId, UserRole.Traveler);
        using var memberClient = factory.CreateAuthenticatedClient(memberUserId, UserRole.Traveler);

        // 1. Host posts broadcast -> member acknowledges twice (idempotent)
        var bResp = await hostClient.PostAsJsonAsync(
            $"/api/v1/travel-groups/{groupId}/messages",
            new SendGroupMessageRequest("broadcast", "Assemble at Gate 1"));
        bResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var broadcast = (await bResp.Content.ReadFromJsonAsync<GroupMessageDto>())!;

        var ack1 = await memberClient.PostAsync(
            $"/api/v1/travel-groups/{groupId}/messages/{broadcast.MessageId}/acknowledge",
            null);
        ack1.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ack1.Content.ReadFromJsonAsync<int>()).Should().Be(1);

        var ack2 = await memberClient.PostAsync(
            $"/api/v1/travel-groups/{groupId}/messages/{broadcast.MessageId}/acknowledge",
            null);
        (await ack2.Content.ReadFromJsonAsync<int>()).Should().Be(1);

        // 2. Host posts roll_call -> member checks in
        var rcResp = await hostClient.PostAsJsonAsync(
            $"/api/v1/travel-groups/{groupId}/messages",
            new SendGroupMessageRequest("roll_call", "Bus #1 Attendance"));
        var rollCall = (await rcResp.Content.ReadFromJsonAsync<GroupMessageDto>())!;

        var checkin = await memberClient.PostAsync(
            $"/api/v1/travel-groups/{groupId}/messages/{rollCall.MessageId}/checkin",
            null);
        checkin.StatusCode.Should().Be(HttpStatusCode.OK);
        (await checkin.Content.ReadFromJsonAsync<int>()).Should().Be(1);

        // 3. Host posts split_bill -> host already paid, member pays
        var billMetadata = $$"""{"totalAmount":800000,"payerName":"Chat Host","payerBankName":"MBBank","payerBankAccount":"1234567890","participants":[{"userId":{{hostUserId}},"displayName":"Chat Host","amount":400000},{"userId":{{memberUserId}},"displayName":"Chat Member","amount":400000}]}""";
        var billResp = await hostClient.PostAsJsonAsync(
            $"/api/v1/travel-groups/{groupId}/messages",
            new SendGroupMessageRequest("split_bill", "Cano Ticket 800k", billMetadata));
        var bill = (await billResp.Content.ReadFromJsonAsync<GroupMessageDto>())!;
        bill.PaidUserIds.Should().Contain(hostUserId);

        var pay = await memberClient.PostAsJsonAsync(
            $"/api/v1/travel-groups/{groupId}/messages/{bill.MessageId}/toggle-payment",
            new TogglePaymentRequest(true));
        pay.StatusCode.Should().Be(HttpStatusCode.OK);
        var paidIds = await pay.Content.ReadFromJsonAsync<IReadOnlyList<long>>();
        paidIds.Should().BeEquivalentTo([hostUserId, memberUserId]);

        // 4. Host posts poll -> member votes option 0 then switches to option 1
        var pollResp = await hostClient.PostAsJsonAsync(
            $"/api/v1/travel-groups/{groupId}/messages",
            new SendGroupMessageRequest("poll", "Where to dine?", "{\"options\":[{\"text\":\"Restaurant\"},{\"text\":\"Market\"}]}"));
        var poll = (await pollResp.Content.ReadFromJsonAsync<GroupMessageDto>())!;

        (await memberClient.PostAsJsonAsync(
                $"/api/v1/travel-groups/{groupId}/messages/{poll.MessageId}/vote",
                new VotePollRequest(0)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var voteSwitch = await memberClient.PostAsJsonAsync(
            $"/api/v1/travel-groups/{groupId}/messages/{poll.MessageId}/vote",
            new VotePollRequest(1));
        voteSwitch.StatusCode.Should().Be(HttpStatusCode.OK);
        var counts = await voteSwitch.Content.ReadFromJsonAsync<Dictionary<int, int>>();
        counts.Should().ContainKey(1).WhoseValue.Should().Be(1);
        counts.Should().NotContainKey(0);

        // 5. Fetch full history + pagination check
        var historyResp = await memberClient.GetAsync($"/api/v1/travel-groups/{groupId}/messages");
        historyResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var history = await historyResp.Content.ReadFromJsonAsync<List<GroupMessageDto>>();
        history.Should().HaveCount(4);
        history![0].ReadByCount.Should().Be(1);
        history[0].IsReadByMe.Should().BeTrue();
        history[1].PresentCount.Should().Be(1);
        history[1].IsPresentByMe.Should().BeTrue();
        history[2].PaidUserIds.Should().BeEquivalentTo([hostUserId, memberUserId]);
        history[3].MyPollVoteOption.Should().Be(1);

        var pagedResp = await memberClient.GetAsync(
            $"/api/v1/travel-groups/{groupId}/messages?limit=2&beforeMessageId={poll.MessageId}");
        var paged = await pagedResp.Content.ReadFromJsonAsync<List<GroupMessageDto>>();
        paged.Should().HaveCount(2);
        paged!.Select(x => x.MessageId).Should().Equal(rollCall.MessageId, bill.MessageId);
    }

    [Fact]
    public async Task OpenApiDocument_IncludesAllGroupMessagesEndpoints()
    {
        await using var factory = new TripMateApiFactory();
        var document = factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);
        using var openApi = JsonDocument.Parse(json);
        var paths = openApi.RootElement.GetProperty("paths");

        paths.TryGetProperty("/api/v1/travel-groups/{groupId}/messages", out var basePath).Should().BeTrue();
        basePath.TryGetProperty("get", out _).Should().BeTrue();
        basePath.TryGetProperty("post", out _).Should().BeTrue();
        paths.TryGetProperty("/api/v1/travel-groups/{groupId}/messages/{messageId}/acknowledge", out _).Should().BeTrue();
        paths.TryGetProperty("/api/v1/travel-groups/{groupId}/messages/{messageId}/checkin", out _).Should().BeTrue();
        paths.TryGetProperty("/api/v1/travel-groups/{groupId}/messages/{messageId}/toggle-payment", out _).Should().BeTrue();
        paths.TryGetProperty("/api/v1/travel-groups/{groupId}/messages/{messageId}/vote", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GroupChatHub_JoinGroupChat_EnforcesActiveMembership()
    {
        await using var factory = new TripMateApiFactory();
        var (_, memberUserId, outsiderUserId, groupId, _) = await SeedTwoGroupsAsync(factory);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TestApiDbContext>();

        var fakeGroups = new RecordingGroupManager();

        // Outsider rejected
        var outsiderHub = new GroupChatHub(dbContext)
        {
            Context = new FakeHubCallerContext(outsiderUserId, "conn-outsider"),
            Groups = fakeGroups,
        };
        var act = async () => await outsiderHub.JoinGroupChat(groupId);
        await act.Should().ThrowAsync<HubException>();
        fakeGroups.AddedRooms.Should().BeEmpty();

        // Active member succeeds
        var memberHub = new GroupChatHub(dbContext)
        {
            Context = new FakeHubCallerContext(memberUserId, "conn-member"),
            Groups = fakeGroups,
        };
        await memberHub.JoinGroupChat(groupId);
        fakeGroups.AddedRooms.Should().ContainSingle()
            .Which.Should().Be(("conn-member", GroupChatHub.GetGroupRoom(groupId)));
    }

    [Fact]
    public async Task SecurityAndValidation_MemberBroadcastDenied_InvalidSplitBillRejected_OutsiderNo404Oracle()
    {
        await using var factory = new TripMateApiFactory();
        var (hostUserId, memberUserId, outsiderUserId, groupId, _) = await SeedTwoGroupsAsync(factory);
        using var hostClient = factory.CreateAuthenticatedClient(hostUserId, UserRole.Traveler);
        using var memberClient = factory.CreateAuthenticatedClient(memberUserId, UserRole.Traveler);
        using var outsiderClient = factory.CreateAuthenticatedClient(outsiderUserId, UserRole.Traveler);

        // Regular Traveler member cannot send broadcast or roll_call
        (await memberClient.PostAsJsonAsync(
                $"/api/v1/travel-groups/{groupId}/messages",
                new SendGroupMessageRequest("broadcast", "Unauthorized broadcast")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await memberClient.PostAsJsonAsync(
                $"/api/v1/travel-groups/{groupId}/messages",
                new SendGroupMessageRequest("roll_call", "Unauthorized roll call")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Invalid split_bill metadata rejected with 400
        (await hostClient.PostAsJsonAsync(
                $"/api/v1/travel-groups/{groupId}/messages",
                new SendGroupMessageRequest("split_bill", "Bad bill", "{}")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await hostClient.PostAsJsonAsync(
                $"/api/v1/travel-groups/{groupId}/messages",
                new SendGroupMessageRequest("split_bill", "Null metadata bill")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Seed real broadcast + poll
        var bMsg = (await (await hostClient.PostAsJsonAsync(
                $"/api/v1/travel-groups/{groupId}/messages",
                new SendGroupMessageRequest("broadcast", "Real notice")))
            .Content.ReadFromJsonAsync<GroupMessageDto>())!;

        // Outsider gets 403 (never 404) for both real and non-existent message IDs
        foreach (var msgId in new[] { bMsg.MessageId, 99999999L })
        {
            (await outsiderClient.PostAsync($"/api/v1/travel-groups/{groupId}/messages/{msgId}/acknowledge", null))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await outsiderClient.PostAsync($"/api/v1/travel-groups/{groupId}/messages/{msgId}/checkin", null))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await outsiderClient.PostAsJsonAsync($"/api/v1/travel-groups/{groupId}/messages/{msgId}/vote", new VotePollRequest(0)))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await outsiderClient.PostAsJsonAsync($"/api/v1/travel-groups/{groupId}/messages/{msgId}/toggle-payment", new TogglePaymentRequest(true)))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
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

    private sealed class RecordingGroupManager : IGroupManager
    {
        public List<(string ConnectionId, string GroupName)> AddedRooms { get; } = [];

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            AddedRooms.Add((connectionId, groupName));
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHubCallerContext(long userId, string connectionId) : HubCallerContext
    {
        private readonly Dictionary<object, object?> _items = [];

        public override string ConnectionId => connectionId;
        public override string? UserIdentifier => userId.ToString();
        public override ClaimsPrincipal User { get; } = new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"));
        public override IDictionary<object, object?> Items => _items;
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}