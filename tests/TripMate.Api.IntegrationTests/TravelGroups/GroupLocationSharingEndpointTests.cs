using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.TravelGroups.LocationSharing;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class GroupLocationSharingEndpointTests
{
    [Fact]
    public async Task LockedTraveler_ExistingTokenCannotUseAnyLocationEndpoint()
    {
        await using var factory = new TripMateApiFactory();
        var (groupId, userId) = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.Traveler);
        var basePath = $"/api/v1/travel-groups/{groupId}";
        var enabled = await client.PutAsJsonAsync($"{basePath}/location-sharing", new { enabled = true });
        var setting = (await enabled.Content.ReadFromJsonAsync<LocationSharingResponse>())!;
        await factory.WithDbContextAsync(async db =>
        {
            (await db.Users.FindAsync(userId))!.Status = AccountStatus.Locked;
            await db.SaveChangesAsync();
            return true;
        });

        (await client.GetAsync($"{basePath}/location-sharing")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync($"{basePath}/locations")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/v1/travel-groups/location-sharing/active"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync($"{basePath}/location-sharing", new { enabled = false }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync($"{basePath}/location", new
        {
            latitude = 16m,
            longitude = 108m,
            sessionVersion = setting.SessionVersion,
        })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.DeleteAsync($"{basePath}/location")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
    [Fact]
    public async Task Owner_EnablesPublishesAndDisables_LocationsNeverLeakAfterOptOut()
    {
        await using var factory = new TripMateApiFactory();
        var (groupId, userId) = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.Traveler);
        var basePath = $"/api/v1/travel-groups/{groupId}";

        var beforeOptIn = await client.PutAsJsonAsync($"{basePath}/location", new
        {
            latitude = 16.047079m,
            longitude = 108.206230m,
            sessionVersion = "0",
        });
        beforeOptIn.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var enabled = await client.PutAsJsonAsync($"{basePath}/location-sharing", new { enabled = true });
        enabled.StatusCode.Should().Be(HttpStatusCode.OK);
        var enabledSetting = (await enabled.Content.ReadFromJsonAsync<LocationSharingResponse>())!;
        enabledSetting.Enabled.Should().BeTrue();
        var active = await client.GetFromJsonAsync<EnabledLocationSharingGroupsResponse>(
            "/api/v1/travel-groups/location-sharing/active");
        active!.GroupIds.Should().ContainSingle().Which.Should().Be(groupId);

        var published = await client.PutAsJsonAsync($"{basePath}/location", new
        {
            latitude = 16.047079m,
            longitude = 108.206230m,
            sessionVersion = enabledSetting.SessionVersion,
        });
        published.StatusCode.Should().Be(HttpStatusCode.OK);

        var locations = await client.GetFromJsonAsync<GroupLocationsResponse>($"{basePath}/locations");
        locations!.Locations.Should().ContainSingle();

        var disabled = await client.PutAsJsonAsync($"{basePath}/location-sharing", new { enabled = false });
        disabled.StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<EnabledLocationSharingGroupsResponse>(
            "/api/v1/travel-groups/location-sharing/active"))!
            .GroupIds.Should().BeEmpty();
        var afterOptOut = await client.GetFromJsonAsync<GroupLocationsResponse>($"{basePath}/locations");
        afterOptOut!.Locations.Should().BeEmpty();
    }

    [Fact]
    public async Task Outsider_CannotReadSettingsOrSharedCoordinates()
    {
        await using var factory = new TripMateApiFactory();
        var (groupId, _) = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(99, UserRole.Traveler);
        var basePath = $"/api/v1/travel-groups/{groupId}";

        (await client.GetAsync($"{basePath}/location-sharing")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync($"{basePath}/locations")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync($"{basePath}/location-sharing", new { enabled = true }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/v1/travel-groups/location-sharing/active"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PermissionRevoked_ClearsCoordinatesButRetainsOptIn()
    {
        await using var factory = new TripMateApiFactory();
        var (groupId, userId) = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(userId, UserRole.Traveler);
        var basePath = $"/api/v1/travel-groups/{groupId}";
        var enabledResponse = await client.PutAsJsonAsync($"{basePath}/location-sharing", new { enabled = true });
        var enabledSetting = (await enabledResponse.Content.ReadFromJsonAsync<LocationSharingResponse>())!;
        await client.PutAsJsonAsync($"{basePath}/location", new
        {
            latitude = 16.047079m,
            longitude = 108.206230m,
            sessionVersion = enabledSetting.SessionVersion,
        });

        (await client.DeleteAsync($"{basePath}/location")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<GroupLocationsResponse>($"{basePath}/locations"))!
            .Locations.Should().BeEmpty();
        (await client.GetFromJsonAsync<LocationSharingResponse>($"{basePath}/location-sharing"))!
            .Enabled.Should().BeTrue();
    }

    private static async Task<(long GroupId, long UserId)> SeedAsync(TripMateApiFactory factory) =>
        await factory.WithDbContextAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var user = new User
            {
                Email = $"{Guid.NewGuid():N}@example.com",
                FullName = "Host",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var group = TravelGroup.Create(1, user.Id, "Location Group", now);
            db.TravelGroups.Add(group);
            await db.SaveChangesAsync();
            db.GroupMembers.Add(GroupMember.CreateHost(group, user.Id, now));
            await db.SaveChangesAsync();
            return (group.Id, user.Id);
        });
}