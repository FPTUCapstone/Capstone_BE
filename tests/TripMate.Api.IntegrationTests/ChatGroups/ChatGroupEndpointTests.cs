using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.ChatGroups;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
namespace TripMate.Api.IntegrationTests.ChatGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class ChatGroupEndpointTests
{
    [Fact]
    public async Task ExactContractAuthorizationIdempotencyAndPaging()
    {
        await using var factory = new TripMateApiFactory();
        await factory.WithDbContextAsync(async db =>
        {
            db.Users.AddRange(Enumerable.Range(301, 5).Select(i => new User { Id = i, FullName = $"Member {i}", Email = $"private-{i}@example.test", Status = i == 305 ? AccountStatus.Locked : AccountStatus.Active }));
            await db.SaveChangesAsync(); return true;
        });
        using var host = factory.CreateAuthenticatedClient(301, UserRole.Traveler);
        using var member = factory.CreateAuthenticatedClient(302, UserRole.Traveler);
        using var outsider = factory.CreateAuthenticatedClient(304, UserRole.Traveler);
        using var locked = factory.CreateAuthenticatedClient(305, UserRole.Traveler);
        using var anonymous = factory.CreateClient();
        const string root = "/api/v1/chat-groups";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(root)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(root, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await locked.GetAsync(root)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostAsJsonAsync(root, new { name = "Group", memberUserIds = new[] { 302, 303 } })).StatusCode);
        var key = Guid.NewGuid();
        var request = new { name = "  Group  ", memberUserIds = new[] { 302, 303 }, clientRequestId = key };
        var response = await host.PostAsJsonAsync(root, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "groupId", "groupName", "hostUserId", "isClosed", "lastMessage", "lastMessageAtUtc", "memberCount", "unreadCount" }, json.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.False(json.GetProperty("isClosed").GetBoolean());
        Assert.DoesNotContain("private-", json.ToString());
        var id = json.GetProperty("groupId").GetInt64();
        var replay = await (await host.PostAsJsonAsync(root, request)).Content.ReadFromJsonAsync<ChatGroupConversationDto>();
        Assert.Equal(id, replay!.GroupId);
        Assert.Equal(HttpStatusCode.Conflict, (await host.PostAsJsonAsync(root, new { name = "Changed", memberUserIds = new[] { 302, 303 }, clientRequestId = key })).StatusCode);
        Assert.Empty((await outsider.GetFromJsonAsync<ChatGroupPage>(root))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"{root}/{id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"{root}/{id}/messages", new { content = "No", clientMessageId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"{root}/{id}/read", new { throughMessageId = 1 })).StatusCode);
        var messageKey = Guid.NewGuid();
        var sent = await host.PostAsJsonAsync($"{root}/{id}/messages", new { content = " Hello ", clientMessageId = messageKey });
        var msg = await sent.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "clientMessageId", "content", "createdAtUtc", "groupId", "messageId", "senderName", "senderUserId" }, msg.EnumerateObject().Select(p => p.Name).Order().ToArray());
        var messageId = msg.GetProperty("messageId").GetInt64();
        Assert.Equal("Hello", msg.GetProperty("content").GetString());
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsJsonAsync($"{root}/{id}/messages", new { content = "Hello", clientMessageId = messageKey })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.PostAsJsonAsync($"{root}/{id}/messages", new { content = "Changed", clientMessageId = messageKey })).StatusCode);
        Assert.Equal(1, (await member.GetFromJsonAsync<ChatGroupPage>(root))!.Items.Single().UnreadCount);
        Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsJsonAsync($"{root}/{id}/read", new { throughMessageId = long.MaxValue })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await member.PostAsJsonAsync($"{root}/{id}/read", new { throughMessageId = messageId })).StatusCode);
        Assert.Equal(0, (await member.GetFromJsonAsync<ChatGroupPage>(root))!.Items.Single().UnreadCount);
        Assert.Single((await member.GetFromJsonAsync<ChatGroupMessageDto[]>($"{root}/{id}/messages"))!);
        Assert.Equal(HttpStatusCode.BadRequest, (await member.GetAsync(root + "?limit=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await member.GetAsync($"{root}/{id}/messages?beforeMessageId=0")).StatusCode);
        var second = await (await host.PostAsJsonAsync(root, new { name = "Second", memberUserIds = new[] { 302, 303 }, clientRequestId = Guid.NewGuid() })).Content.ReadFromJsonAsync<ChatGroupConversationDto>();
        var page = await member.GetFromJsonAsync<ChatGroupPage>(root + "?limit=1");
        Assert.Equal(second!.GroupId, Assert.Single(page!.Items).GroupId);
        Assert.Equal(second.GroupId, page.NextBeforeGroupId);
        var older = await member.GetFromJsonAsync<ChatGroupPage>(root + $"?limit=1&beforeGroupId={page.NextBeforeGroupId}");
        Assert.Equal(id, Assert.Single(older!.Items).GroupId);
        Assert.Null(older.NextBeforeGroupId);
        var wrongGroup = await host.PostAsJsonAsync($"{root}/{second.GroupId}/read", new { throughMessageId = messageId });
        Assert.Equal(HttpStatusCode.BadRequest, wrongGroup.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await locked.PostAsJsonAsync($"{root}/{id}/messages", new { content = "No", clientMessageId = Guid.NewGuid() })).StatusCode);
    }
}