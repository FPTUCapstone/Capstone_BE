using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Friendships;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Friendships;

[Collection(nameof(TripMateApiFactory))]
public sealed class FriendshipEndpointTests
{
    [Fact]
    public async Task AuthenticatedLifecycleReturnsExactContractAndKeepsChatOpen()
    {
        await using var factory = new TripMateApiFactory();
        await factory.WithDbContextAsync(async db =>
        {
            db.Users.AddRange(new User { Id = 301, FullName = "Alice", Status = AccountStatus.Active },
                new User { Id = 302, FullName = "Bob", Status = AccountStatus.Active },
                new User { Id = 303, FullName = "Bobby", Status = AccountStatus.Locked },
                new User { Id = 304, FullName = "Outsider", Status = AccountStatus.Active });
            await db.SaveChangesAsync();
            return true;
        });
        using var alice = factory.CreateAuthenticatedClient(301, UserRole.Traveler);
        using var bob = factory.CreateAuthenticatedClient(302, UserRole.Traveler);
        using var outsider = factory.CreateAuthenticatedClient(304, UserRole.Traveler);
        using var anonymous = factory.CreateClient();
        const string root = "/api/v1/friendships";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(root)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync($"{root}/302", new { action = "request" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PutAsJsonAsync($"{root}/301", new { action = "request" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PutAsJsonAsync($"{root}/302", new { action = "bad" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PutAsJsonAsync($"{root}/302", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PutAsJsonAsync($"{root}/303", new { action = "request" })).StatusCode);
        Assert.Empty((await alice.GetFromJsonAsync<FriendshipDto[]>(root))!);
        Assert.Single((await alice.GetFromJsonAsync<JsonElement>("/api/v1/direct-conversations/users?search=Bo")).EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/v1/direct-conversations", new { otherUserId = 302 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.PutAsJsonAsync($"{root}/302", new { action = "request" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await bob.PutAsJsonAsync($"{root}/301", new { action = "request" })).StatusCode);
        var json = await bob.GetFromJsonAsync<JsonElement>(root);
        var row = Assert.Single(json.EnumerateArray());
        Assert.Equal(new[] { "avatarUrl", "displayName", "requestedByUserId", "status", "userId" }, row.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("Pending", row.GetProperty("status").GetString());
        Assert.Equal(301, row.GetProperty("requestedByUserId").GetInt64());
        Assert.Equal(301, row.GetProperty("userId").GetInt64());
        Assert.Empty((await outsider.GetFromJsonAsync<FriendshipDto[]>(root))!);
        Assert.Equal(HttpStatusCode.Conflict, (await outsider.PutAsJsonAsync($"{root}/301", new { action = "accept" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsJsonAsync($"{root}/302", new { action = "accept" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsJsonAsync($"{root}/302", new { action = "decline" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PutAsJsonAsync($"{root}/301", new { action = "cancel" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await bob.PutAsJsonAsync($"{root}/301", new { action = "accept" })).StatusCode);
        Assert.Equal("Accepted", Assert.Single((await alice.GetFromJsonAsync<FriendshipDto[]>(root))!).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.PutAsJsonAsync($"{root}/302", new { action = "remove" })).StatusCode);
        Assert.Empty((await bob.GetFromJsonAsync<FriendshipDto[]>(root))!);
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsJsonAsync("/api/v1/direct-conversations", new { otherUserId = 301 })).StatusCode);
    }
}