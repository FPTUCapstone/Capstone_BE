using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.DirectChat;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.DirectChat;

[Collection(nameof(TripMateApiFactory))]
public sealed class DirectChatEndpointTests
{
    [Fact]
    public async Task PaginationAndValidationStayWithinOwnedConversations()
    {
        await using var factory = new TripMateApiFactory();
        await factory.WithDbContextAsync(async db =>
        {
            db.Users.AddRange(Enumerable.Range(201, 4).Select(id => new User
            {
                Id = id,
                FullName = $"User {id}",
                Status = AccountStatus.Active,
                Role = UserRole.Traveler
            }));
            await db.SaveChangesAsync();
            return true;
        });
        using var client = factory.CreateAuthenticatedClient(201, UserRole.Traveler);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/direct-conversations", new { otherUserId = 201 })).StatusCode);
        foreach (var otherUserId in new[] { 202, 203, 204 })
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/direct-conversations", new { otherUserId })).StatusCode);
        var page = (await client.GetFromJsonAsync<DirectConversationPage>("/api/v1/direct-conversations?limit=2"))!;
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(page.Items[1].ConversationId, page.NextBeforeConversationId);
        var tail = (await client.GetFromJsonAsync<DirectConversationPage>($"/api/v1/direct-conversations?limit=2&beforeConversationId={page.NextBeforeConversationId}"))!;
        Assert.Single(tail.Items);
        Assert.Null(tail.NextBeforeConversationId);
        var id = page.Items[0].ConversationId;
        var path = $"/api/v1/direct-conversations/{id}/messages";
        foreach (var query in new[] { "limit=0", "limit=101", "beforeMessageId=0", "beforeMessageId=-1" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{path}?{query}")).StatusCode);
        foreach (var content in new string?[] { null, " ", new string('x', 4001) })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { content, clientMessageId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { content = "Hello", clientMessageId = Guid.Empty })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { content = "Hello", clientMessageId = "invalid" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/v1/direct-conversations/{id}/read", new { throughMessageId = long.MaxValue })).StatusCode);
        using var peer = factory.CreateAuthenticatedClient(202, UserRole.Traveler);
        Assert.Single((await peer.GetFromJsonAsync<DirectConversationPage>("/api/v1/direct-conversations"))!.Items);
    }

    [Fact]
    public async Task DirectChatPersistsRetriesReadCursorsAndPrivateProjections()
    {
        await using var factory = new TripMateApiFactory();
        await factory.WithDbContextAsync(async db =>
        {
            db.Users.AddRange(new User { Id = 101, FullName = "Alice", Status = AccountStatus.Active, Role = UserRole.Administrator },
                new User { Id = 102, FullName = "Bob", Status = AccountStatus.Active, Email = "private@example.test" },
                new User { Id = 103, FullName = "Bobby", Status = AccountStatus.Locked },
                new User { Id = 104, FullName = "Outsider", Status = AccountStatus.Active });
            await db.SaveChangesAsync();
            return true;
        });
        using var alice = factory.CreateAuthenticatedClient(101, UserRole.Administrator);
        using var bob = factory.CreateAuthenticatedClient(102, UserRole.Traveler);
        using var outsider = factory.CreateAuthenticatedClient(104, UserRole.Traveler);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/direct-conversations")).StatusCode);
        var search = await alice.GetFromJsonAsync<JsonElement>("/api/v1/direct-conversations/users?search=%20Bo%20");
        Assert.Single(search.EnumerateArray());
        Assert.Equal(new[] { "avatarUrl", "displayName", "userId" }, search[0].EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync("/api/v1/direct-conversations/users?search=b")).StatusCode);
        var started = await alice.PostAsJsonAsync("/api/v1/direct-conversations", new { otherUserId = 102 });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var conversationJson = await started.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "avatarUrl", "conversationId", "displayName", "lastMessage", "lastMessageAtUtc", "otherUserId", "unreadCount" },
            conversationJson.EnumerateObject().Select(p => p.Name).Order().ToArray());
        var conversation = conversationJson.Deserialize<DirectConversationDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var id = conversation.ConversationId;
        var reverse = await bob.PostAsJsonAsync("/api/v1/direct-conversations", new { otherUserId = 101 });
        Assert.Equal(id, (await reverse.Content.ReadFromJsonAsync<DirectConversationDto>())!.ConversationId);
        var path = $"/api/v1/direct-conversations/{id}/messages";
        var key = Guid.NewGuid();
        var sent = await alice.PostAsJsonAsync(path, new { content = "  Hello Bob  ", clientMessageId = key });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var messageJson = await sent.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "clientMessageId", "content", "conversationId", "createdAtUtc", "isReadByOther", "messageId", "senderName", "senderUserId" },
            messageJson.EnumerateObject().Select(p => p.Name).Order().ToArray());
        var first = messageJson.Deserialize<DirectMessageDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("Hello Bob", first.Content);
        Assert.False(first.IsReadByOther);
        var retry = await alice.PostAsJsonAsync(path, new { content = "Hello Bob", clientMessageId = key });
        Assert.Equal(first.MessageId, (await retry.Content.ReadFromJsonAsync<DirectMessageDto>())!.MessageId);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync(path, new { content = "Changed", clientMessageId = key })).StatusCode);
        var sent2 = await alice.PostAsJsonAsync(path, new { content = "Newer", clientMessageId = Guid.NewGuid() });
        var second = (await sent2.Content.ReadFromJsonAsync<DirectMessageDto>())!;
        Assert.Equal(2, (await bob.GetFromJsonAsync<DirectConversationPage>("/api/v1/direct-conversations"))!.Items.Single().UnreadCount);
        Assert.Equal(HttpStatusCode.NoContent, (await bob.PostAsJsonAsync($"/api/v1/direct-conversations/{id}/read", new { throughMessageId = first.MessageId })).StatusCode);
        Assert.Equal(1, (await bob.GetFromJsonAsync<DirectConversationPage>("/api/v1/direct-conversations"))!.Items.Single().UnreadCount);
        var messages = (await alice.GetFromJsonAsync<DirectMessageDto[]>(path))!;
        Assert.Equal(2, messages.Length);
        Assert.True(messages[0].IsReadByOther);
        Assert.False(messages[1].IsReadByOther);
        Assert.Single((await alice.GetFromJsonAsync<DirectMessageDto[]>($"{path}?limit=1&beforeMessageId={second.MessageId}"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync(path, new { content = "No", clientMessageId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"/api/v1/direct-conversations/{id}/read", new { throughMessageId = first.MessageId })).StatusCode);
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.FindAsync(102L))!.Status = AccountStatus.Locked;
            await db.SaveChangesAsync();
            return true;
        });
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync(path)).StatusCode);
        Assert.Empty((await alice.GetFromJsonAsync<DirectConversationPage>("/api/v1/direct-conversations"))!.Items);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/v1/direct-conversations")).StatusCode);
    }
}