using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.TravelGroups.GetMembers;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class GetTravelGroupMembersEndpointTests
{
    [Fact]
    public async Task GetMembers_ActiveMember_ReturnsOnlyActiveMembers()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.MemberUserId, UserRole.Traveler);

        var response = await client.GetAsync($"/api/v1/travel-groups/{seed.GroupId}/members");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<GetTravelGroupMembersResponse>();
        payload.Should().NotBeNull();
        payload!.GroupId.Should().Be(seed.GroupId);
        payload.MemberCount.Should().Be(2);
        payload.Members.Should().ContainSingle(member => member.IsHost);
        payload.Members.Select(member => member.DisplayName)
            .Should().NotContain("Removed Traveler");
    }

    [Fact]
    public async Task GetMembers_TravelerOutsideGroup_ReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.MemberUserId + 100, UserRole.Traveler);

        var response = await client.GetAsync($"/api/v1/travel-groups/{seed.GroupId}/members");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetMembers_MissingGroup_ReturnsNotFound()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        var response = await client.GetAsync("/api/v1/travel-groups/999999/members");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<(long GroupId, long MemberUserId)> SeedAsync(TripMateApiFactory factory) =>
        await factory.WithDbContextAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var host = await AddTravelerAsync(db, "Host Traveler", now);
            var member = await AddTravelerAsync(db, "Member Traveler", now);
            var removed = await AddTravelerAsync(db, "Removed Traveler", now);
            var group = TravelGroup.Create(1, host.Id, "Endpoint Test Group", now);
            db.TravelGroups.Add(group);
            await db.SaveChangesAsync();

            db.GroupMembers.Add(GroupMember.CreateHost(group, host.Id, now.AddHours(-2)));
            db.GroupMembers.Add(GroupMember.CreateMember(group, member.Id, now.AddHours(-1)));
            var removedMembership = GroupMember.CreateMember(group, removed.Id, now);
            typeof(GroupMember).GetProperty(nameof(GroupMember.Status))!
                .SetValue(removedMembership, GroupMemberStatus.Removed);
            db.GroupMembers.Add(removedMembership);
            await db.SaveChangesAsync();

            return (group.Id, member.Id);
        });

    private static async Task<User> AddTravelerAsync(
        TestApiDbContext db,
        string fullName,
        DateTimeOffset now)
    {
        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@example.com",
            FullName = fullName,
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}