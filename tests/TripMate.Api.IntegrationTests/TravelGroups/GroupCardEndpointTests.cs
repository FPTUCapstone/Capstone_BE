using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.ChatGroups;
using TripMate.Application.Features.TravelGroups.Chat;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class GroupCardEndpointTests
{
    [Fact]
    public async Task EditVotesImportantAndIndependentMembersExposeExactAuthorizedContract()
    {
        await using var factory = new TripMateApiFactory();
        var group = await factory.WithDbContextAsync(async db =>
        {
            db.Users.AddRange(Enumerable.Range(601, 4).Select(i => new User { Id = i, FullName = $"Member {i}", Email = $"private{i}@example.test", Role = UserRole.Traveler, Status = AccountStatus.Active }));
            var g = TravelGroup.Create(100, 601, "Cards", DateTimeOffset.UtcNow);
            g.AddMember(GroupMember.CreateHost(g, 601, DateTimeOffset.UtcNow));
            g.AddMember(GroupMember.CreateMember(g, 602, DateTimeOffset.UtcNow));
            g.AddMember(GroupMember.CreateMember(g, 603, DateTimeOffset.UtcNow));
            db.TravelGroups.Add(g); await db.SaveChangesAsync(); return g.Id;
        });
        using var host = factory.CreateAuthenticatedClient(601, UserRole.Traveler);
        using var member = factory.CreateAuthenticatedClient(602, UserRole.Traveler);
        using var outsider = factory.CreateAuthenticatedClient(604, UserRole.Traveler);
        using var anonymous = factory.CreateClient();
        var root = $"/api/v1/travel-groups/{group}/messages";
        const string poll = """{"options":[{"id":"a","text":"A"},{"id":"b","text":"B"}]}""";
        var response = await host.PostAsJsonAsync(root, new { messageType = "poll", content = "Question", metadataJson = poll, clientMessageId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var message = (await response.Content.ReadFromJsonAsync<GroupMessageDto>())!;
        Assert.Equal(0, message.Revision); Assert.Null(message.EditedAtUtc);
        var route = root + "/" + message.MessageId;
        var edit = new { content = "Question", metadataJson = poll, expectedRevision = 0, clientRequestId = Guid.NewGuid() };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(route, edit)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PutAsJsonAsync(route, edit)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync(route, edit)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PutAsJsonAsync(route, new { content = "Question", metadataJson = "bad", expectedRevision = 0, clientRequestId = Guid.NewGuid() })).StatusCode);
        var changed = await host.PutAsJsonAsync(route, edit); Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var updated = (await changed.Content.ReadFromJsonAsync<GroupMessageDto>())!; Assert.Equal(1, updated.Revision); Assert.NotNull(updated.EditedAtUtc);
        Assert.Equal(1, (await (await host.PutAsJsonAsync(route, edit)).Content.ReadFromJsonAsync<GroupMessageDto>())!.Revision);
        Assert.Equal(HttpStatusCode.Conflict, (await host.PutAsJsonAsync(route, new { content = "Stale", metadataJson = poll, expectedRevision = 0, clientRequestId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await member.PostAsJsonAsync(route + "/vote", new { optionIndex = 1, expectedRevision = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.PostAsJsonAsync(route + "/vote", new { optionIndex = 1, expectedRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(route + "/votes")).StatusCode);
        var voters = await host.GetFromJsonAsync<JsonElement>(route + "/votes"); var voter = Assert.Single(voters.EnumerateArray());
        Assert.Equal(new[] { "avatarUrl", "displayName", "optionIndex", "userId", "votedAtUtc" }, voter.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("Member 602", voter.GetProperty("displayName").GetString()); Assert.DoesNotContain("private", voters.ToString());
        const string appended = """{"options":[{"id":"a","text":"A"},{"id":"b","text":"B"},{"id":"c","text":"C"}]}""";
        var append = await host.PutAsJsonAsync(route, new { content = "Question", metadataJson = appended, expectedRevision = 1, clientRequestId = Guid.NewGuid() }); Assert.Equal(HttpStatusCode.OK, append.StatusCode);
        var deny = await host.PutAsJsonAsync(route, new { content = "Different question", metadataJson = appended, expectedRevision = 2, clientRequestId = Guid.NewGuid() }); Assert.Equal(HttpStatusCode.Conflict, deny.StatusCode);
        var problem = await deny.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("travel_group.message_edit_conflict", problem.GetProperty("errorCode").GetString());
        await host.PostAsJsonAsync(root, new { messageType = "broadcast", content = "Notice" });
        for (var i = 0; i < 4; i++) await host.PostAsJsonAsync(root, new { messageType = "text", content = "Recent" });
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(root + "/important")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync(root + "/important?limit=101")).StatusCode);
        var page = (await member.GetFromJsonAsync<ImportantGroupMessagePage>(root + "/important?limit=1"))!;
        Assert.Equal("broadcast", Assert.Single(page.Items).MessageType); Assert.NotNull(page.NextBeforeMessageId);
        var next = (await member.GetFromJsonAsync<ImportantGroupMessagePage>(root + "/important?limit=1&beforeMessageId=" + page.NextBeforeMessageId))!;
        Assert.Equal(message.MessageId, Assert.Single(next.Items).MessageId); Assert.Null(next.NextBeforeMessageId);
        var chat = (await (await host.PostAsJsonAsync("/api/v1/chat-groups", new { name = "Members", memberUserIds = new[] { 602, 603 }, clientRequestId = Guid.NewGuid() })).Content.ReadFromJsonAsync<ChatGroupConversationDto>())!;
        var membersRoute = $"/api/v1/chat-groups/{chat.GroupId}/members";
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(membersRoute)).StatusCode);
        var members = await member.GetFromJsonAsync<JsonElement>(membersRoute);
        Assert.Equal(new[] { "groupId", "groupName", "isClosed", "memberCount", "members" }, members.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.False(members.GetProperty("isClosed").GetBoolean());
        var first = members.GetProperty("members")[0];
        Assert.Equal(new[] { "avatarUrl", "canReceiveHost", "displayName", "isHost", "userId" }, first.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.True(first.GetProperty("isHost").GetBoolean()); Assert.DoesNotContain("private", members.ToString());
        await factory.WithDbContextAsync(async db => { (await db.GroupMembers.SingleAsync(m => m.GroupId == group && m.UserId == 602)).Leave(DateTimeOffset.UtcNow); return await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(route + "/votes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(root + "/important")).StatusCode);
    }
}