using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.TravelGroups.RemoveGroupMember;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class RemoveGroupMemberEndpointTests
{
    [Fact]
    public async Task Remove_RequiresTravelerAuthenticationAndValidKey()
    {
        await using var factory = new TripMateApiFactory();
        using var anonymous = factory.CreateClient();
        (await anonymous.DeleteAsync("/api/v1/travel-groups/1/members/2"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var operatorClient = factory.CreateAuthenticatedClient(1, UserRole.TourOperator);
        operatorClient.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await operatorClient.DeleteAsync("/api/v1/travel-groups/1/members/2"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var traveler = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        (await traveler.DeleteAsync("/api/v1/travel-groups/1/members/2"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        traveler.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await traveler.DeleteAsync("/api/v1/travel-groups/1/members/1"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await traveler.DeleteAsync("/api/v1/travel-groups/abc/members/2"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await traveler.DeleteAsync("/api/v1/travel-groups/1/members/abc"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Remove_SucceedsAndReplaysSameResponse()
    {
        await using var factory = new TripMateApiFactory();
        var ids = await factory.WithDbContextAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var host = new User { Email = $"remove-host-{Guid.NewGuid():N}@example.com", FullName = "Host", Role = UserRole.Traveler, Status = AccountStatus.Active, CreatedAtUtc = now, UpdatedAtUtc = now };
            var target = new User { Email = $"remove-target-{Guid.NewGuid():N}@example.com", FullName = "Target", Role = UserRole.Traveler, Status = AccountStatus.Active, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Users.AddRange(host, target);
            await db.SaveChangesAsync();
            var group = TravelGroup.Create(1, host.Id, "Group", now);
            db.TravelGroups.Add(group);
            db.GroupMembers.AddRange(GroupMember.CreateHost(group, host.Id, now), GroupMember.CreateMember(group, target.Id, now));
            await db.SaveChangesAsync();
            return (group.Id, host.Id, target.Id);
        });

        using var client = factory.CreateAuthenticatedClient(ids.Item2, UserRole.Traveler);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var path = $"/api/v1/travel-groups/{ids.Item1}/members/{ids.Item3}";
        var first = await client.DeleteAsync(path);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await first.Content.ReadFromJsonAsync<RemoveGroupMemberResponse>();
        firstBody!.MemberName.Should().Be("Target");
        using var removedClient = factory.CreateAuthenticatedClient(ids.Item3, UserRole.Traveler);
        (await removedClient.GetAsync($"/api/v1/travel-groups/{ids.Item1}/members"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var inbox = await removedClient.GetAsync("/api/v1/notifications?unreadOnly=true&limit=20");
        inbox.StatusCode.Should().Be(HttpStatusCode.OK);
        (await inbox.Content.ReadAsStringAsync()).Should().Contain("TravelGroupMemberRemoved");
        var replay = await client.DeleteAsync(path);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadFromJsonAsync<RemoveGroupMemberResponse>()).Should().Be(firstBody);
        var mismatch = await client.DeleteAsync($"/api/v1/travel-groups/{ids.Item1}/members/{ids.Item3 + 1}");
        mismatch.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var newKeyClient = factory.CreateAuthenticatedClient(ids.Item2, UserRole.Traveler);
        newKeyClient.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await newKeyClient.DeleteAsync(path)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await newKeyClient.DeleteAsync($"/api/v1/travel-groups/{ids.Item1 + 100}/members/{ids.Item3}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var nonHost = factory.CreateAuthenticatedClient(ids.Item3, UserRole.Traveler);
        nonHost.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await nonHost.DeleteAsync($"/api/v1/travel-groups/{ids.Item1}/members/{ids.Item2}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}