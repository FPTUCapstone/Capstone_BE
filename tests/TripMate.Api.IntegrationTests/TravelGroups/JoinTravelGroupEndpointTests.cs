using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.Controllers.V1.Requests;
using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Application.Features.TravelGroups.JoinTravelGroup;
using TripMate.Domain.Constants;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class JoinTravelGroupEndpointTests
{
    [Fact]
    public async Task Join_WithoutAuthentication_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/travel-groups/join", new JoinTravelGroupRequest("A7K4P2QX"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Join_WithoutIdempotencyKey_ReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        var response = await client.PostAsJsonAsync("/api/v1/travel-groups/join", new JoinTravelGroupRequest("A7K4P2QX"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Join_WithMalformedIdempotencyKey_ReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "not-a-guid");

        var response = await client.PostAsJsonAsync("/api/v1/travel-groups/join", new JoinTravelGroupRequest("A7K4P2QX"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Join_WhenCallerIsNotTraveler_ReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.TourOperator);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/v1/travel-groups/join", new JoinTravelGroupRequest("A7K4P2QX"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Join_WhenCodeIsEmpty_ReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/v1/travel-groups/join", new JoinTravelGroupRequest(""));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Join_WhenCodeNotFound_ReturnsBadRequestWithInvitationUnavailable()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/v1/travel-groups/join", new JoinTravelGroupRequest("NOTEXIST"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(TravelGroupErrorCodes.InvitationUnavailable);
    }

    [Fact]
    public async Task Join_WhenValidInvitation_Returns200AndCreatesMembership()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);

        var joiningUserId = seed.HostUserId + 10;
        using var client = factory.CreateAuthenticatedClient(joiningUserId, UserRole.Traveler);
        var idempotencyKey = Guid.NewGuid();
        client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey.ToString());

        var response = await client.PostAsJsonAsync(
            "/api/v1/travel-groups/join",
            new JoinTravelGroupRequest(seed.InviteCode));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JoinTravelGroupResponse>();
        payload.Should().NotBeNull();
        payload!.GroupId.Should().Be(seed.GroupId);
        payload.GroupName.Should().Be(seed.GroupName);

        // Replay with identical key returns OK with same payload
        using var replayClient = factory.CreateAuthenticatedClient(joiningUserId, UserRole.Traveler);
        replayClient.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey.ToString());
        var replayResponse = await replayClient.PostAsJsonAsync(
            "/api/v1/travel-groups/join",
            new JoinTravelGroupRequest(seed.InviteCode));
        replayResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Join_WhenAlreadyActiveMember_ReturnsConflictWithGroupIdExtension_AndLeavesUsageUnchanged()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);

        var joiningUserId = seed.HostUserId + 10;

        // First join succeeds and creates active membership
        using var client1 = factory.CreateAuthenticatedClient(joiningUserId, UserRole.Traveler);
        client1.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var firstResponse = await client1.PostAsJsonAsync(
            "/api/v1/travel-groups/join",
            new JoinTravelGroupRequest(seed.InviteCode));
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second join with different idempotency key returns 409 Conflict
        using var client2 = factory.CreateAuthenticatedClient(joiningUserId, UserRole.Traveler);
        client2.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var conflictResponse = await client2.PostAsJsonAsync(
            "/api/v1/travel-groups/join",
            new JoinTravelGroupRequest(seed.InviteCode));

        conflictResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await conflictResponse.Content.ReadAsStringAsync();
        body.Should().Contain(TravelGroupErrorCodes.AlreadyActiveMember);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var extensions = root.GetProperty("extensions");
        extensions.GetProperty("errorCode").GetString().Should().Be(TravelGroupErrorCodes.AlreadyActiveMember);
        extensions.GetProperty("groupId").GetInt64().Should().Be(seed.GroupId);

        // Verify membership count and invitation usage remain unchanged
        await factory.WithDbContextAsync(async db =>
        {
            var memberCount = await db.GroupMembers.CountAsync(
                m => m.GroupId == seed.GroupId && m.UserId == joiningUserId);
            memberCount.Should().Be(1);

            var invitation = await db.GroupInvitations.FirstAsync(
                i => i.GroupId == seed.GroupId);
            invitation.UsedCount.Should().Be(1);
            return true;
        });
    }

    private static async Task<(long GroupId, string GroupName, long HostUserId, string InviteCode)> SeedAsync(
        TripMateApiFactory factory) =>
        await factory.WithDbContextAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var host = new User
            {
                Email = $"host-{Guid.NewGuid():N}@example.com",
                FullName = "Join Endpoint Host",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            db.Users.Add(host);
            await db.SaveChangesAsync();

            var group = TravelGroup.Create(1, host.Id, "Endpoint Test Group", now);
            db.TravelGroups.Add(group);
            await db.SaveChangesAsync();

            var inviteCode = $"J{Guid.NewGuid():N}"[..8].ToUpperInvariant();
            var invitation = GroupInvitation.Create(
                group.Id,
                host.Id,
                inviteCode,
                now.AddDays(7),
                TravelGroupConstants.UnlimitedInvitationUses,
                now);
            db.GroupInvitations.Add(invitation);
            await db.SaveChangesAsync();

            return (group.Id, group.Name, host.Id, inviteCode);
        });
}