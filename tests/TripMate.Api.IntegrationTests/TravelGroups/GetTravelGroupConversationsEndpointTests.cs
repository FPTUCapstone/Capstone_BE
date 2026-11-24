using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.TravelGroups.GetConversations;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class GetTravelGroupConversationsEndpointTests
{
    private const string Route = "/api/v1/travel-groups/conversations";

    [Theory]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.TourOperator)]
    public async Task Get_AllowedRole_UsesAuthenticatedMembershipAndReturnsExactContract(UserRole role)
    {
        await using var factory = new TripMateApiFactory();
        var groupId = await AddGroupAsync(factory, 10);
        await AddGroupAsync(factory, 20);
        using var client = factory.CreateAuthenticatedClient(10, role);

        var response = await client.GetAsync(Route + "?userId=20");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("items", "nextBeforeGroupId");
        var items = json.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        items[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "groupId", "groupName", "itineraryId", "itineraryTitle", "itinerarySourceType",
            "hostUserId", "memberCount", "status", "lastMessage", "lastMessageAtUtc", "unreadCount");
        items[0].GetProperty("unreadCount").GetInt32().Should().Be(0);
        items[0].GetProperty("groupId").GetInt64().Should().Be(groupId);
        items[0].GetProperty("itinerarySourceType").GetString().Should().Be("Manual");
        items[0].GetProperty("status").GetString().Should().Be("Active");
        items[0].GetProperty("lastMessage").ValueKind.Should().Be(JsonValueKind.Null);
        items[0].GetProperty("lastMessageAtUtc").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Get_UnreadCount_ExcludesReadOwnAndOtherGroupMessages()
    {
        await using var factory = new TripMateApiFactory();
        var groupId = await AddGroupAsync(factory, 10);
        var unrelatedId = await AddGroupAsync(factory, 20);
        await factory.WithDbContextAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var read = new GroupMessage(groupId, 20, nameof(GroupMessageType.Text), "Already read", null, now);
            db.GroupMessages.Add(read);
            await db.SaveChangesAsync();
            db.GroupMembers.Single(m => m.GroupId == groupId && m.UserId == 10)
                .AdvanceChatReadCursor(read.Id);
            db.GroupMessages.Add(new GroupMessage(groupId, 20, nameof(GroupMessageType.Text), "Unread incoming", null, now));
            db.GroupMessages.Add(new GroupMessage(groupId, 10, nameof(GroupMessageType.Text), "Own later message", null, now));
            db.GroupMessages.Add(new GroupMessage(unrelatedId, 20, nameof(GroupMessageType.Text), "Other group", null, now));
            await db.SaveChangesAsync();
            return true;
        });
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Traveler);

        var page = await client.GetFromJsonAsync<GetTravelGroupConversationsResponse>(Route + "?limit=1");

        page!.Items.Should().ContainSingle().Which.UnreadCount.Should().Be(1);
        page.Items[0].GroupId.Should().Be(groupId);
        page.NextBeforeGroupId.Should().BeNull();
    }

    [Fact]
    public async Task Get_Anonymous_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        (await client.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.Staff)]
    [InlineData(UserRole.ServiceProvider)]
    public async Task Get_OtherRole_ReturnsForbidden(UserRole role)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(10, role);
        (await client.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=-1")]
    [InlineData("?limit=101")]
    [InlineData("?limit=2147483647")]
    [InlineData("?limit=abc")]
    [InlineData("?beforeGroupId=0")]
    [InlineData("?beforeGroupId=-1")]
    [InlineData("?beforeGroupId=abc")]
    [InlineData("?beforeGroupId=9223372036854775808")]
    public async Task Get_InvalidPaging_ReturnsBadRequest(string query)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Traveler);
        (await client.GetAsync(Route + query)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_Pages_ReturnsExclusiveCursorAndNullOnFinalPage()
    {
        await using var factory = new TripMateApiFactory();
        var firstId = await AddGroupAsync(factory, 10);
        var secondId = await AddGroupAsync(factory, 10);
        using var client = factory.CreateAuthenticatedClient(10, UserRole.Traveler);

        var first = await client.GetFromJsonAsync<GetTravelGroupConversationsResponse>(Route + "?limit=1");
        first!.Items.Should().ContainSingle(i => i.GroupId == secondId);
        first.NextBeforeGroupId.Should().Be(secondId);
        var second = await client.GetFromJsonAsync<GetTravelGroupConversationsResponse>(Route + $"?limit=1&beforeGroupId={secondId}");
        second!.Items.Should().ContainSingle(i => i.GroupId == firstId);
        second.NextBeforeGroupId.Should().BeNull();
    }

    [Fact]
    public async Task Get_Operator_TravelerLifecycleEndpointsRemainTravelerOnly()
    {
        await using var factory = new TripMateApiFactory();
        var groupId = await AddGroupAsync(factory, 10);
        using var client = factory.CreateAuthenticatedClient(10, UserRole.TourOperator);

        using var leaveReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/travel-groups/{groupId}/leave");
        leaveReq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await client.SendAsync(leaveReq)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var joinReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/travel-groups/join")
        {
            Content = JsonContent.Create(new { invitationCode = "ABCD1234" }),
        };
        joinReq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await client.SendAsync(joinReq)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_Operator_NonMemberOnMembersEndpoint_ReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        var groupId = await AddGroupAsync(factory, 10);
        using var client = factory.CreateAuthenticatedClient(999, UserRole.TourOperator);

        (await client.GetAsync($"/api/v1/travel-groups/{groupId}/members"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static Task<long> AddGroupAsync(TripMateApiFactory factory, long userId) =>
        factory.WithDbContextAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var itinerary = Itinerary.CreateManual(userId, "Endpoint itinerary", Itinerary.ActiveStatus, now);
            db.Itineraries.Add(itinerary);
            await db.SaveChangesAsync();
            var group = TravelGroup.Create(itinerary.Id, userId, "Endpoint group", now);
            db.TravelGroups.Add(group);
            db.GroupMembers.Add(GroupMember.CreateHost(group, userId, now));
            await db.SaveChangesAsync();
            return group.Id;
        });
}