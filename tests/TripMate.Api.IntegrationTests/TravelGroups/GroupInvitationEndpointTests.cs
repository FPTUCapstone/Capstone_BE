using System.Net;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class GroupInvitationEndpointTests
{
    [Fact]
    public async Task GetOrCreateInvitation_WithoutAuthentication_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/v1/travel-groups/1/invitation", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetOrCreateInvitation_WithoutIdempotencyKey_ReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.HostUserId, UserRole.Traveler);

        var response = await client.PostAsync($"/api/v1/travel-groups/{seed.GroupId}/invitation", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetOrCreateInvitation_WithMalformedIdempotencyKey_ReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.HostUserId, UserRole.Traveler);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "not-a-guid");

        var response = await client.PostAsync($"/api/v1/travel-groups/{seed.GroupId}/invitation", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetOrCreateInvitation_WhenCallerIsNotHost_ReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.HostUserId + 1, UserRole.Traveler);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsync($"/api/v1/travel-groups/{seed.GroupId}/invitation", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetOrCreateInvitation_WhenGroupDoesNotExist_ReturnsDocumentedNotFoundCode()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(999_999, UserRole.Traveler);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.PostAsync("/api/v1/travel-groups/999999/invitation", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString()
            .Should().Be("travel_group.group_not_found");
    }

    [Fact]
    public async Task GetOrCreateInvitation_WithRepeatedKey_ReturnsSameInvitation()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.HostUserId, UserRole.Traveler);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var firstResponse = await client.PostAsync($"/api/v1/travel-groups/{seed.GroupId}/invitation", null);
        var retryResponse = await client.PostAsync($"/api/v1/travel-groups/{seed.GroupId}/invitation", null);

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        retryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadInviteCodeAsync(retryResponse)).Should().Be(await ReadInviteCodeAsync(firstResponse));
        (await factory.WithDbContextAsync(context => context.GroupInvitations.CountAsync())).Should().Be(1);
        (await factory.WithDbContextAsync(context => context.GroupInvitationOperations.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task RegenerateInvitation_ExpiresPreviousCodeAndReturnsReplacement()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.HostUserId, UserRole.Traveler);

        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var firstResponse = await client.PostAsync($"/api/v1/travel-groups/{seed.GroupId}/invitation", null);
        var firstCode = await ReadInviteCodeAsync(firstResponse);

        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var regenerateResponse = await client.PostAsync(
            $"/api/v1/travel-groups/{seed.GroupId}/invitation/regenerate",
            null);

        regenerateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadInviteCodeAsync(regenerateResponse)).Should().NotBe(firstCode);
        (await factory.WithDbContextAsync(context => context.GroupInvitations.CountAsync())).Should().Be(2);
    }

    [Fact]
    public async Task RegenerateInvitation_WhenKeyWasUsedForGetOrCreate_ReturnsConflict()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.HostUserId, UserRole.Traveler);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var getOrCreate = await client.PostAsync($"/api/v1/travel-groups/{seed.GroupId}/invitation", null);
        var regenerate = await client.PostAsync(
            $"/api/v1/travel-groups/{seed.GroupId}/invitation/regenerate",
            null);

        getOrCreate.StatusCode.Should().Be(HttpStatusCode.OK);
        regenerate.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private static async Task<(long HostUserId, long GroupId)> SeedAsync(TripMateApiFactory factory) =>
        await factory.WithDbContextAsync(async context =>
        {
            var now = DateTimeOffset.UtcNow;
            var host = new User
            {
                Email = $"invitation-host-{Guid.NewGuid():N}@example.com",
                FullName = "Invitation Host",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            context.Users.Add(host);
            await context.SaveChangesAsync();

            var group = TravelGroup.Create(1, host.Id, "Invitation Endpoint Group", now);
            context.TravelGroups.Add(group);
            await context.SaveChangesAsync();
            return (host.Id, group.Id);
        });

    private static async Task<string> ReadInviteCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("inviteCode").GetString()!;
    }
}